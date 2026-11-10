using System.Globalization;
using RepoBackup.Cli;
using RepoBackup.Core.Application;
using RepoBackup.Core.Backup;
using RepoBackup.Core.Discovery;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class BackupEfficiencyTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        await tests.Run("Read policy isolates successful parents and enforces the seven-day full reread", () =>
        {
            var now = DateTimeOffset.UtcNow; var last = now.AddDays(-6);
            SnapshotInfo Parent(string id, DateTimeOffset at) => new(id, now.AddMinutes(-1),
                ["repobackup", "successful", "series=project-a", "coverage=full", "full-read-at=" + at.UtcTicks.ToString(CultureInfo.InvariantCulture)], []) { Hostname = "fixture" };
            var parent = Parent("good", last);
            BackupReadDecision Choose(IEnumerable<SnapshotInfo> snapshots, BackupReadMode mode = BackupReadMode.Incremental) =>
                BackupReadPolicy.Select(snapshots, "project-a", Coverage.FullProject, "fixture", mode, now);
            Assert(Choose([parent]) == new BackupReadDecision(BackupReadMode.Incremental, "good", last), "Eligible parent ignored.");
            foreach (var invalid in new[] { Parent("old", now.AddDays(-7)), Parent("future", now.AddDays(1)),
                parent with { Hostname = "other" }, parent with { Tags = [.. parent.Tags, "pending"] },
                parent with { Tags = ["successful", "series=project-a", "coverage=full", "full-read-at=garbage"] },
                parent with { Tags = ["successful", "series=selection-a", "coverage=partial"] },
                parent with { Tags = ["repobackup", "pending", "series=project-a", "coverage=full"] } })
                Assert(Choose([invalid]).Mode == BackupReadMode.Full, "Unsafe incremental baseline: " + Json.Write(invalid));
            Assert(Choose([]).Mode == BackupReadMode.Full && Choose([parent], BackupReadMode.Full).Mode == BackupReadMode.Full, "Full reread override failed.");
            return Task.CompletedTask;
        });

        foreach (var protection in Enum.GetValues<DestinationProtection>())
            await IntegrationAsync(tests, Path.Combine(root, protection.ToString()), protection);
        await StagingAsync(tests, Path.Combine(root, "staging"));
    }

    private static async Task IntegrationAsync(TestRunner tests, string root, DestinationProtection protection)
    {
        var provider = new CountingProvider();
        var app = new ApplicationServices(Path.Combine(root, "catalog"), discoveryProviders: [provider]);
        var projects = new List<ProjectEntry>();
        for (var i = 0; i < 2; i++)
        {
            var source = Path.Combine(root, "source-" + i); Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "file.txt"), "Original data");
            projects.Add(app.Catalog.RegisterCandidate("Efficiency " + i, [source], "Manual", approved: true));
        }
        var destination = await app.Backups.AddDestinationAsync("Efficiency", Path.Combine(root, "repository"), protection: protection);
        var runner = new RecordingRunner();
        var client = new ResticClient(app.Paths, app.Credentials, runner, app.Restic.Executable);
        var service = new BackupService(app.Paths, app.Catalog, app.Credentials, client, app.Planner, app.Discovery);
        await tests.Run(protection + ": repository identity mismatch preserves the exception contract and releases the lease", async () =>
        {
            await Throws<InvalidDataException>(() => service.RunAsync(destination with { RepositoryId = new string('0', 64) }, projects));
            using var released = DestinationLease.Acquire(app.Paths, destination.Path);
        });
        await tests.Run(protection + ": batch checks once, streams output, avoids discovery and removes staging", async () =>
        {
            var jobs = await service.RunAsync(destination, projects);
            Assert(jobs.All(RunOutcome.IsSuccessful) && jobs.All(j => j.ReadMode == BackupReadMode.Full), Json.Write(jobs));
            Assert(runner.Count("check") == 1 && runner.Count("backup") == 2 && runner.Count("snapshots") == 2 && provider.Reads == 0, "Repeated repository work or discovery.");
            Assert(runner.BackupsStreamed && runner.Calls.Where(c => c.Contains("backup")).All(c => c.Contains("--no-scan")), "Backup output or scanner not streamed.");
            Assert(!Directory.EnumerateFileSystemEntries(app.Paths.JobsDirectory).Any(), "Staging files accumulated.");
        });

        await tests.Run(protection + ": incremental reuse and forced rereads preserve expected file contents", async () =>
        {
            runner.Calls.Clear();
            var first = (await app.Restic.SnapshotsAsync(destination)).First(s => s.TagValue("series") == "project-" + projects[0].Id);
            var job = (await service.RunAsync(destination, [projects[0]])).Single();
            Assert(job.ReadMode == BackupReadMode.Incremental && job.ParentSnapshotId == first.Id && RunOutcome.IsSuccessful(job), Json.Write(job));
            var args = runner.Calls.Single(c => c.Contains("backup"));
            Assert(args.Contains("--parent") && !args.Contains("--force"), "Incremental data was forcibly reread.");
            var source = Path.Combine(projects[0].Roots[0].Path, "file.txt"); var time = File.GetLastWriteTimeUtc(source);
            await File.WriteAllTextAsync(source, "Modified data"); File.SetLastWriteTimeUtc(source, time);
            var full = (await service.RunAsync(destination, [projects[0]], readMode: BackupReadMode.Full)).Single();
            Assert(full.ReadMode == BackupReadMode.Full && full.ParentSnapshotId is null && RunOutcome.IsSuccessful(full), Json.Write(full));
            var snapshot = (await app.Restic.SnapshotsAsync(destination)).Single(s => s.Id == full.SnapshotId);
            var target = Path.Combine(root, "forced-restore"); await app.Restore.RestoreAsync(destination, snapshot, target);
            Assert(await File.ReadAllTextAsync(Directory.GetFiles(Path.Combine(target, "sources"), "file.txt", SearchOption.AllDirectories).Single()) == "Modified data", "Full reread missed preserved-timestamp edit.");
            await File.AppendAllTextAsync(source, " plus edit");
            var modified = (await service.RunAsync(destination, [projects[0]])).Single();
            var contents = await app.Restic.RunAsync(destination, ["dump", modified.SnapshotId!, PathSafety.SnapshotPath(source)]);
            contents.EnsureSuccess("Read incrementally modified file");
            Assert(RunOutcome.IsSuccessful(modified) && modified.ReadMode == BackupReadMode.Incremental
                && contents.Output.TrimEnd('\r', '\n') == "Modified data plus edit", "Incremental backup missed an in-place edit.");
            await File.WriteAllTextAsync(Path.Combine(projects[0].Roots[0].Path, "added.txt"), "New file");
            File.Delete(source);
            var edited = (await service.RunAsync(destination, [projects[0]])).Single();
            var editedSnapshot = (await app.Restic.SnapshotsAsync(destination)).Single(s => s.Id == edited.SnapshotId);
            var editedTarget = Path.Combine(root, "incremental-restore"); await app.Restore.RestoreAsync(destination, editedSnapshot, editedTarget);
            var files = Directory.GetFiles(Path.Combine(editedTarget, "sources"), "*", SearchOption.AllDirectories);
            Assert(files.Length == 1 && Path.GetFileName(files[0]) == "added.txt", "Incremental snapshot resurrected removed files.");
            Assert(editedSnapshot.TagValue("full-read-at") == snapshot.TagValue("full-read-at"), "Incremental lost the full-reread date.");
        });

        await tests.Run(protection + ": cancellation leaves the whole unverified batch pending", async () =>
        {
            using var cancel = new CancellationTokenSource(); runner.Calls.Clear();
            var jobs = await service.RunAsync(destination, projects, progress: new ProgressRecorder(p => { if (p.Stage == "Awaiting verification") cancel.Cancel(); }), token: cancel.Token);
            Assert(jobs.All(j => j.FinishedAt is not null) && jobs.All(j => j.Backup == Outcome.Cancelled) && runner.Count("check") == 0 && runner.Count("forget") == 0, Json.Write(jobs));
            var pending = (await app.Restic.SnapshotsAsync(destination)).Single(s => s.TagValue("run") == jobs[0].Id);
            Assert(!pending.Successful && pending.Tags.Contains("pending") && RunOutcome.ExitCode(jobs) == 130, "Cancelled pending snapshot marked successful.");
        });

        await tests.Run(protection + ": unchanged locked files cannot claim successful incremental coverage", async () =>
        {
            var file = Path.Combine(projects[0].Roots[0].Path, "added.txt");
            using var locked = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var job = (await service.RunAsync(destination, [projects[0]])).Single();
            Assert(job.ReadMode == BackupReadMode.Incremental && job.Backup == Outcome.Incomplete && job.Cleanup == Outcome.NotRun
                && job.Messages.Any(m => m.Contains("Source unavailable")), Json.Write(job));
        });

        await tests.Run(protection + ": verification failure blocks all finalization and retention", async () =>
        {
            runner.Calls.Clear(); runner.Fail = "check";
            var jobs = await service.RunAsync(destination, projects); runner.Fail = null;
            Assert(jobs.All(j => j.Backup == Outcome.Failed && j.Verification == Outcome.Failed && j.Cleanup == Outcome.NotRun), Json.Write(jobs));
            Assert(runner.Count("check") == 1 && runner.Count("tag") == 0 && runner.Count("forget") == 0, "Unverified batch finalized.");
        });

        await tests.Run(protection + ": creation failure is isolated and cancellation during verification blocks finalization", async () =>
        {
            runner.Calls.Clear(); runner.Fail = "backup"; runner.FailOnce = true;
            var jobs = await service.RunAsync(destination, projects);
            Assert(jobs[0].Backup == Outcome.Failed && jobs[0].Verification == Outcome.NotRun && RunOutcome.IsSuccessful(jobs[1]) && runner.Count("check") == 1, Json.Write(jobs));
            using var cancel = new CancellationTokenSource(); runner.Calls.Clear();
            jobs = await service.RunAsync(destination, projects, progress: new ProgressRecorder(p => { if (p.Stage == "Verifying repository") cancel.Cancel(); }), token: cancel.Token);
            Assert(jobs.All(j => j.Backup == Outcome.Cancelled && j.Verification == Outcome.Cancelled && j.Cleanup == Outcome.NotRun) && runner.Count("tag") == 0, Json.Write(jobs));
        });

        await tests.Run(protection + ": one tagging failure does not prevent other projects finalizing", async () =>
        {
            runner.Calls.Clear(); runner.Fail = "tag"; runner.FailOnce = true;
            var jobs = await service.RunAsync(destination, projects);
            Assert(jobs[0].Backup == Outcome.Failed && jobs[0].Verification == Outcome.Successful && RunOutcome.IsSuccessful(jobs[1]), Json.Write(jobs));
        });

        await tests.Run(protection + ": committed tagging is reconciled after cancellation", async () =>
        {
            using var cancel = new CancellationTokenSource(); runner.CancelAfterTag = cancel;
            var job = (await service.RunAsync(destination, [projects[0]], token: cancel.Token)).Single(); runner.CancelAfterTag = null;
            Assert(RunOutcome.HasRecoveryPoint(job) && job.Cleanup == Outcome.Cancelled && RunOutcome.ExitCode([job]) == 130, Json.Write(job));
            Assert((await app.Restic.SnapshotsAsync(destination)).Any(s => s.Id == job.SnapshotId && s.Successful), "Committed snapshot identity lost.");
        });

        await tests.Run(protection + ": retention batches forget/prune and retries a failed prune", async () =>
        {
            // Small fixtures; create enough successful points for both series.
            for (var i = 0; i < 10; i++)
                Assert((await service.RunAsync(destination, projects)).All(RunOutcome.IsSuccessful), "Retention setup failed.");
            runner.Calls.Clear(); runner.Fail = "prune";
            var jobs = await service.RunAsync(destination, projects); runner.Fail = null;
            Assert(jobs.All(j => RunOutcome.HasRecoveryPoint(j) && j.Cleanup == Outcome.Failed), Json.Write(jobs));
            Assert(runner.Count("check") == 1 && runner.Count("forget") == 1 && runner.Count("prune") == 1, "Maintenance repeated per project.");
            var snapshots = await app.Restic.SnapshotsAsync(destination);
            foreach (var project in projects) Assert(snapshots.Count(s => s.Successful && s.TagValue("series") == "project-" + project.Id) == 10, "Retention crossed series.");
            runner.Calls.Clear(); runner.Fail = "forget";
            jobs = await service.RunAsync(destination, projects); runner.Fail = null;
            Assert(jobs.All(j => RunOutcome.HasRecoveryPoint(j) && j.Cleanup == Outcome.Failed) && runner.Count("forget") == 1 && runner.Count("prune") == 0, "Failed forget incorrectly pruned or invalidated a recovery point.");
            var extraPath = Path.Combine(root, "retry-source"); Directory.CreateDirectory(extraPath); await File.WriteAllTextAsync(Path.Combine(extraPath, "x"), "retry");
            var extra = app.Catalog.RegisterCandidate("Retry", [extraPath], "Manual", approved: true);
            runner.Calls.Clear();
            var retry = (await service.RunAsync(destination, [extra])).Single();
            Assert(RunOutcome.IsSuccessful(retry) && runner.Count("forget") == 0 && runner.Count("prune") == 1, "Pending prune was forgotten.");
        });

        await tests.Run(protection + ": CLI overrides the shared default and rejects invalid read modes", async () =>
        {
            app.Catalog.SaveSetting(BackupReadPolicy.Setting, BackupReadMode.Full);
            Assert((await service.RunAsync(destination, [projects[0]])).Single().ReadMode == BackupReadMode.Full, "Shared default ignored.");
            using var output = new StringWriter(); using var errors = new StringWriter();
            var args = new[] { "--data-dir", app.Paths.DataDirectory, "backup", "--destination", destination.Id, "--project", projects[0].Id, "--read-mode", "incremental" };
            Assert(await CommandHost.RunAsync(args, output, errors) == 0, errors.ToString());
            Assert(Json.Read<List<JobRecord>>(output.ToString()).Single().ReadMode == BackupReadMode.Incremental, "CLI override ignored.");
            args[^1] = "invalid"; Assert(await CommandHost.RunAsync(args, TextWriter.Null, errors) == 1, "Invalid read mode accepted.");
            var schedule = new ScheduleDefinition { DestinationId = destination.Id, Enabled = true };
            app.Catalog.SaveSchedules([schedule]);
            Assert(await CommandHost.RunAsync(["--data-dir", app.Paths.DataDirectory, "run-schedule", "--id", schedule.Id], TextWriter.Null, errors) == 0, errors.ToString());
            Assert(app.Catalog.History().OrderByDescending(j => j.StartedAt).Take(3).All(j => j.ReadMode == BackupReadMode.Full), "Scheduled backups ignored the shared full-read default.");
        });
    }

    private static async Task StagingAsync(TestRunner tests, string root)
    {
        var paths = new AppPaths(root);
        await tests.Run("Staging writes literal UTF-8 NUL paths and preserves unknown files and active jobs", async () =>
        {
            var job = new JobRecord { ProjectId = "p", ProjectName = "p", SeriesId = "p", DestinationId = "d", FinishedAt = DateTimeOffset.UtcNow };
            var folder = Path.Combine(paths.JobsDirectory, job.Id); Directory.CreateDirectory(folder);
            await BackupStaging.WriteFileListAsync(Path.Combine(folder, "files.raw"), ["C:\\file[1] é.txt", "C:\\line\nbreak"], default);
            Assert(await File.ReadAllTextAsync(Path.Combine(folder, "files.raw")) == "C:\\file[1] é.txt\0C:\\line\nbreak\0", "File list encoded incorrectly.");
            BackupStaging.Sweep(paths, [job], _ => { });
            Assert(File.Exists(Path.Combine(folder, "files.raw")), "Active owner staging deleted.");
            await File.WriteAllTextAsync(Path.Combine(folder, "keep.txt"), "Unknown diagnostic");
            Assert(BackupStaging.Clean(paths, job.Id) is null && File.Exists(Path.Combine(folder, "keep.txt")) && !File.Exists(Path.Combine(folder, "files.raw")), "Cleanup removed unknown files.");
            Assert(BackupStaging.Clean(paths, "../escape") is not null, "Unsafe staging identity accepted.");
            File.Delete(Path.Combine(folder, "keep.txt"));
            BackupStaging.Sweep(paths, [job with { OwnerProcessId = int.MaxValue }], _ => { });
            Assert(!Directory.Exists(folder), "Inactive terminal staging not swept.");
        });
        await tests.Run("Staging cleanup refuses junctions and preserves outside data", async () =>
        {
            var external = Path.Combine(root, "outside"); Directory.CreateDirectory(external);
            await File.WriteAllTextAsync(Path.Combine(external, "manifest.json"), "must survive");
            var job = Guid.NewGuid().ToString("N"); var link = Path.Combine(paths.JobsDirectory, job);
            string Quote(string value) => "'" + value.Replace("'", "''") + "'";
            (await new ProcessRunner().RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "New-Item -ItemType Junction -Path " + Quote(link) + " -Target " + Quote(external) + " | Out-Null"]))
                .EnsureSuccess("Create staging junction fixture");
            Assert(BackupStaging.Clean(paths, job) is not null && File.Exists(Path.Combine(external, "manifest.json")), "Staging cleanup followed a junction.");
        });
    }

    private sealed class CountingProvider : IProjectDiscoveryProvider
    {
        public string Id => "counting";
        public int Reads { get; private set; }
        public Task<ProviderDiscoveryResult> ReadAsync(CancellationToken token = default)
        { Reads++; throw new InvalidOperationException("Discovery must not run during backup."); }
    }

    private sealed class RecordingRunner : ProcessRunner
    {
        public List<string[]> Calls { get; } = [];
        public string? Fail { get; set; }
        public bool FailOnce { get; set; }
        public bool BackupsStreamed { get; private set; } = true;
        public CancellationTokenSource? CancelAfterTag { get; set; }
        public int Count(string command) => Calls.Count(c => c.Contains(command));
        public override Task<ProcessResult> RunStreamAsync(string executable, IEnumerable<string> arguments, Func<Stream, CancellationToken, Task> consume,
            CancellationToken token = default, IReadOnlyDictionary<string, string?>? environment = null, string? secret = null)
        {
            var args = arguments.ToArray(); Calls.Add(args);
            if (Fail is { } fail && args.Contains(fail))
            {
                if (FailOnce) { Fail = null; FailOnce = false; }
                return Task.FromResult(new ProcessResult(1, "", "Injected " + fail + " failure"));
            }
            return base.RunStreamAsync(executable, args, consume, token, environment, secret);
        }
        public override async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken token = default,
            string? workingDirectory = null, IReadOnlyDictionary<string, string?>? environment = null, Action<string>? onOutput = null, string? secret = null, bool captureOutput = true)
        {
            var args = arguments.ToArray(); Calls.Add(args);
            if (args.Contains("backup")) BackupsStreamed &= !captureOutput && onOutput is not null;
            if (Fail is { } fail && args.Contains(fail))
            {
                if (FailOnce) { Fail = null; FailOnce = false; }
                return new(1, "", "Injected " + fail + " failure");
            }
            var result = await base.RunAsync(executable, args, token, workingDirectory, environment, onOutput, secret, captureOutput);
            if (args.Contains("tag") && CancelAfterTag is { } cancel) { cancel.Cancel(); throw new OperationCanceledException(token); }
            return result;
        }
    }
}
