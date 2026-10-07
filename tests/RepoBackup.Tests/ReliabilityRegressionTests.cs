using RepoBackup.Cli;
using RepoBackup.Core.Application;
using RepoBackup.Core.Backup;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Git;
using RepoBackup.Core.Models;
using RepoBackup.Core.Recovery;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class ReliabilityRegressionTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        var app = new ApplicationServices(Path.Combine(root, "catalog"), discoveryProviders: []);
        var source = Path.Combine(root, "source[1]"); Directory.CreateDirectory(source);
        var requested = Path.Combine(source, "note[1].txt"); var decoy = Path.Combine(source, "note1.txt");
        await File.WriteAllTextAsync(requested, "Exact bracketed file"); await File.WriteAllTextAsync(decoy, "Wrong file");
        var folder = Path.Combine(source, "assets[1]"); Directory.CreateDirectory(folder); await File.WriteAllTextAsync(Path.Combine(folder, "inside.txt"), "Exact folder");
        var decoyFolder = Path.Combine(source, "assets1"); Directory.CreateDirectory(decoyFolder); await File.WriteAllTextAsync(Path.Combine(decoyFolder, "wrong.txt"), "Wrong folder");
        var project = app.Catalog.RegisterCandidate("Regression fixture", [source], "Manual", approved: true);
        var destination = await app.Backups.AddDestinationAsync("Regression repository", Path.Combine(root, "repository"), protection: DestinationProtection.PasswordFree);
        var full = (await app.Backups.RunAsync(destination, [project])).Single();
        var snapshot = (await app.Restic.SnapshotsAsync(destination)).Single();
        await tests.Run("Bracketed selective restore restores exactly the requested file and hash", async () =>
        {
            var target = Path.Combine(root, "exact-file");
            await app.Restore.RestoreAsync(destination, snapshot, target, PathSafety.SnapshotPath(requested));
            var files = Directory.GetFiles(Path.Combine(target, "sources"), "*", SearchOption.AllDirectories);
            Assert(files.Length == 1 && Path.GetFileName(files[0]) == "note[1].txt" && await Fixture.HashAsync(files[0]) == await Fixture.HashAsync(requested), "Literal filename was interpreted as a pattern.");
        });
        await tests.Run("Missing snapshot paths cannot create a target or record restore success", async () =>
        {
            var before = app.Catalog.GetSetting<System.Text.Json.JsonElement>("lastRestore").ToString();
            var target = Path.Combine(root, "missing-file");
            await Throws<ArgumentException>(() => app.Restore.RestoreAsync(destination, snapshot, target, PathSafety.SnapshotPath(Path.Combine(source, "missing.txt"))));
            Assert(!Directory.Exists(target) && app.Catalog.GetSetting<System.Text.Json.JsonElement>("lastRestore").ToString() == before, "Missing file restore claimed success.");
        });
        await tests.Run("Bracketed folder restores exclude the similarly named sibling", async () =>
        {
            var target = Path.Combine(root, "exact-folder"); await app.Restore.RestoreAsync(destination, snapshot, target, PathSafety.SnapshotPath(folder));
            var files = Directory.GetFiles(Path.Combine(target, "sources"), "*", SearchOption.AllDirectories);
            Assert(files.Length == 1 && Path.GetFileName(files[0]) == "inside.txt" && await Fixture.HashAsync(files[0]) == await Fixture.HashAsync(Path.Combine(folder, "inside.txt")), "Folder selection restored unrelated contents.");
        });
        await tests.Run("A restore process reporting success without requested contents cannot record success", async () =>
        {
            var before = app.Catalog.GetSetting<System.Text.Json.JsonElement>("lastRestore").ToString();
            var client = new ResticClient(app.Paths, app.Credentials, new OverrideRunner(args => args.Contains("restore") ? new ProcessResult(0, "", "") : null), app.Restic.Executable);
            var restore = new RestoreService(app.Paths, app.Catalog, client, new GitRecovery(new GitInspector(new ProcessRunner())));
            var target = Path.Combine(root, "silent-restore-failure");
            await Throws<IOException>(() => restore.RestoreAsync(destination, snapshot, target, PathSafety.SnapshotPath(requested)));
            Assert(File.Exists(Path.Combine(target, "RESTORE-INCOMPLETE.txt")) && app.Catalog.GetSetting<System.Text.Json.JsonElement>("lastRestore").ToString() == before, "An empty restore was recorded as successful.");
        });
        var selection = new SavedSelection { ProjectId = project.Id, Name = "Exact file", Paths = [requested] };
        await tests.Run("Bracketed individual-file selections support whole and selective restores", async () =>
        {
            app.Catalog.SaveSelection(selection);
            var job = (await app.Backups.RunAsync(destination, [project], selection)).Single(); Assert(RunOutcome.IsSuccessful(job), Json.Write(job));
            var selectedSnapshot = (await app.Restic.SnapshotsAsync(destination)).Single(s => s.TagValue("series") == "selection-" + selection.Id);
            foreach (var selective in new[] { false, true })
            {
                var target = Path.Combine(root, "file-selection-" + selective);
                await app.Restore.RestoreAsync(destination, selectedSnapshot, target, selective ? PathSafety.SnapshotPath(requested) : null);
                var files = Directory.GetFiles(Path.Combine(target, "sources"), "*", SearchOption.AllDirectories);
                Assert(files.Length == 1 && Path.GetFileName(files[0]) == "note[1].txt" && await Fixture.HashAsync(files[0]) == await Fixture.HashAsync(requested), "Saved selection restored the wrong file.");
            }
        });
        await tests.Run("Cancellation between projects returns a terminal result for every request", async () =>
        {
            var otherPath = Path.Combine(root, "other"); Directory.CreateDirectory(otherPath); await File.WriteAllTextAsync(Path.Combine(otherPath, "file.txt"), "Other project");
            var other = app.Catalog.RegisterCandidate("Other", [otherPath], "Manual", approved: true);
            using var cancelled = new CancellationTokenSource();
            var jobs = await app.Backups.RunAsync(destination, [project, other], progress: new ImmediateProgress(p => { if (p.Stage == "Successful") cancelled.Cancel(); }), token: cancelled.Token);
            Assert(jobs.Count == 2 && RunOutcome.IsSuccessful(jobs[0]) && jobs[1].Backup == Outcome.Cancelled && jobs.All(j => j.FinishedAt is not null) && RunOutcome.ExitCode(jobs) == 130, Json.Write(jobs));
            Assert(app.Catalog.History().Any(j => j.Id == jobs[1].Id), "Not-started cancellation was omitted from history.");
        });
        await tests.Run("Cancellation during snapshot finalization preserves verification but cannot claim success", async () =>
        {
            using var cancelled = new CancellationTokenSource();
            var job = (await app.Backups.RunAsync(destination, [project], progress: new ImmediateProgress(p => { if (p.Stage == "Finalizing verified snapshot") cancelled.Cancel(); }), token: cancelled.Token)).Single();
            Assert(job.Backup == Outcome.Cancelled && job.Verification == Outcome.Successful && job.Cleanup == Outcome.NotRun && RunOutcome.ExitCode([job]) == 130, Json.Write(job));
            Assert(!(await app.Restic.SnapshotsAsync(destination)).Single(s => s.Id == job.SnapshotId).Successful, "Cancelled finalization tagged the snapshot successful.");
        });
        await tests.Run("Snapshot tagging errors are failed backups with separate successful verification", async () =>
        {
            var restic = new ResticClient(app.Paths, app.Credentials, new TagFailureRunner(), app.Restic.Executable);
            var backups = new BackupService(app.Paths, app.Catalog, app.Credentials, restic, app.Planner, app.Discovery);
            var job = (await backups.RunAsync(destination, [project])).Single();
            Assert(job.Backup == Outcome.Failed && job.Verification == Outcome.Successful && job.Cleanup == Outcome.NotRun && RunOutcome.ExitCode([job]) == 2, Json.Write(job));
        });
        await tests.Run("Cleanup errors remain visible while preserving a verified recovery point", async () =>
        {
            var job = (await app.Backups.RunAsync(destination, [project], progress: new ImmediateProgress(p => { if (p.Stage == "Keeping latest ten versions") throw new IOException("Injected cleanup failure"); }))).Single();
            Assert(RunOutcome.HasRecoveryPoint(job) && job.Cleanup == Outcome.Failed && RunOutcome.ExitCode([job]) == 2 && (await app.Restic.SnapshotsAsync(destination)).Any(s => s.Id == job.SnapshotId && s.Successful), Json.Write(job));
            using var cancelled = new CancellationTokenSource();
            var cleanupCancelled = (await app.Backups.RunAsync(destination, [project], progress: new ImmediateProgress(p => { if (p.Stage == "Keeping latest ten versions") { cancelled.Cancel(); cancelled.Token.ThrowIfCancellationRequested(); } }), token: cancelled.Token)).Single();
            Assert(RunOutcome.HasRecoveryPoint(cleanupCancelled) && cleanupCancelled.Cleanup == Outcome.Cancelled && RunOutcome.ExitCode([cleanupCancelled]) == 130, Json.Write(cleanupCancelled));
        });
        await tests.Run("CLI backup and scheduled runner commands use cancellation exit code 130", async () =>
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var code = await CommandHost.RunAsync(["--data-dir", app.Paths.DataDirectory, "backup", "--destination", destination.Id, "--all-enabled"], TextWriter.Null, TextWriter.Null, cancelled.Token);
            Assert(code == 130, "Cancelled backup returned " + code);
            var schedule = new ScheduleDefinition { DestinationId = destination.Id, Enabled = true }; app.Catalog.SaveSchedules([schedule]);
            code = await CommandHost.RunAsync(["--data-dir", app.Paths.DataDirectory, "run-schedule", "--id", schedule.Id], TextWriter.Null, TextWriter.Null, cancelled.Token);
            Assert(code == 130, "Cancelled runner command returned " + code);
        });
        await tests.Run("Recovery uses newest roots and maps older saved selections to those root identities", async () =>
        {
            var moved = Path.Combine(root, "moved-source"); Directory.Move(source, moved);
            app.Catalog.RelinkRoot(project.Id, project.Roots[0].Id, moved);
            project = app.Catalog.Projects().Single(p => p.Id == project.Id);
            Assert(RunOutcome.IsSuccessful((await app.Backups.RunAsync(destination, [project])).Single()), "Moved backup failed.");
            var fresh = new ApplicationServices(Path.Combine(root, "fresh-catalog"), discoveryProviders: []);
            var opened = await fresh.Backups.AddDestinationAsync("Opened", destination.Path, openExisting: true, protection: DestinationProtection.PasswordFree);
            await fresh.Restore.RebuildCatalogAsync(opened);
            var recovered = fresh.Catalog.Projects().Single(p => p.Id == project.Id);
            var recoveredSelection = fresh.Catalog.Selections().Single(s => s.Id == selection.Id);
            Assert(recovered.Roots[0].Path == moved && !recovered.Enabled && recoveredSelection.Paths.Single() == Path.Combine(moved, "note[1].txt"), "Recovered roots and selection paths are inconsistent.");
            fresh.Catalog.SaveProject(recovered with { Name = "Local edit", Enabled = true });
            fresh.Catalog.SaveSelection(recoveredSelection with { Name = "Local selection edit" });
            await fresh.Restore.RebuildCatalogAsync(opened);
            Assert(fresh.Catalog.Projects().Single(p => p.Id == project.Id).Name == "Local edit" && fresh.Catalog.Selections().Single(s => s.Id == selection.Id).Name == "Local selection edit", "Recovery overwrote local edits.");
        });
        await tests.Run("Catalog recovery validates relationships before committing any recovered data", async () =>
        {
            var fresh = new ApplicationServices(Path.Combine(root, "transaction-catalog"), discoveryProviders: []);
            var manifest = new RecoveryManifest { Project = project, Selection = selection with { ProjectId = project.Id, Paths = [Path.Combine(root, "outside.txt")] }, JobId = "rollback", SeriesId = "selection-" + selection.Id };
            await Throws<InvalidDataException>(() => { fresh.Catalog.RecoverCatalog([manifest], [full]); return Task.CompletedTask; });
            Assert(fresh.Catalog.Projects().Count == 0 && fresh.Catalog.Selections().Count == 0 && fresh.Catalog.History().Count == 0, "Invalid recovery committed partial data.");
        });
        await tests.Run("Recovery falls back to older valid manifests when the newest project metadata is invalid", async () =>
        {
            var fresh = new ApplicationServices(Path.Combine(root, "fallback-catalog"), discoveryProviders: []);
            var opened = await fresh.Backups.AddDestinationAsync("Fallback", destination.Path, openExisting: true, protection: DestinationProtection.PasswordFree);
            var newest = (await app.Restic.SnapshotsAsync(destination)).First(); var manifest = await app.Restic.ManifestAsync(destination, newest);
            var invalid = manifest with { Project = manifest.Project with { Roots = [manifest.Project.Roots[0], manifest.Project.Roots[0]] } };
            var client = new ResticClient(fresh.Paths, fresh.Credentials, new OverrideRunner(args => args.Contains("dump") && args.Contains(newest.Id) ? new ProcessResult(0, Json.Write(invalid), "") : null), fresh.Restic.Executable);
            var restore = new RestoreService(fresh.Paths, fresh.Catalog, client, new GitRecovery(new GitInspector(new ProcessRunner())));
            await restore.RebuildCatalogAsync(opened);
            Assert(fresh.Catalog.Projects().Single().Roots[0].Path == source && !fresh.Catalog.History().Any(j => j.Id == manifest.JobId), "Invalid newest metadata displaced an older valid manifest.");
        });
        await tests.Run("Manual add reactivates dismissed identity while automatic discovery respects dismissal", () =>
        {
            app.Catalog.SaveProject(project with { Dismissed = true, Enabled = false });
            var discovered = app.Catalog.RegisterCandidate("Automatic", [project.Roots[0].Path], "Fixture");
            Assert(discovered.Id == project.Id && discovered.Dismissed && !discovered.Enabled, "Automatic discovery reactivated a dismissal.");
            var reactivated = app.Catalog.RegisterCandidate("Explicit", [project.Roots[0].Path], "Manual", approved: true);
            Assert(reactivated.Id == project.Id && reactivated.Reviewed && reactivated.Enabled && !reactivated.Dismissed, "Explicit add did not reactivate the existing identity.");
            return Task.CompletedTask;
        });
    }
    private sealed class ImmediateProgress(Action<BackupProgress> action) : IProgress<BackupProgress> { public void Report(BackupProgress value) => action(value); }
    private sealed class OverrideRunner(Func<List<string>, ProcessResult?> intercept) : ProcessRunner
    {
        public override Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken token = default, string? workingDirectory = null, IReadOnlyDictionary<string, string>? environment = null, Action<string>? onOutput = null, string? secret = null, bool captureOutput = true)
        {
            var args = arguments.ToList(); var result = intercept(args);
            return result is null ? base.RunAsync(executable, args, token, workingDirectory, environment, onOutput, secret, captureOutput) : Task.FromResult(result);
        }
    }
    private sealed class TagFailureRunner : ProcessRunner
    {
        public override Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken token = default, string? workingDirectory = null, IReadOnlyDictionary<string, string>? environment = null, Action<string>? onOutput = null, string? secret = null, bool captureOutput = true)
        {
            var args = arguments.ToList();
            return args.Contains("tag") ? Task.FromResult(new ProcessResult(1, "", "Injected tag failure")) : base.RunAsync(executable, args, token, workingDirectory, environment, onOutput, secret, captureOutput);
        }
    }
}
