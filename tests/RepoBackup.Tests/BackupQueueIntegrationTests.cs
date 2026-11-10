using System.Collections.Concurrent;
using RepoBackup.Core.Application;
using RepoBackup.Core.Backup;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using RepoBackup.Desktop.Infrastructure;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class BackupQueueIntegrationTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        foreach (var protection in Enum.GetValues<DestinationProtection>())
        {
            var folder = Path.Combine(root, protection.ToString());
            var app = new ApplicationServices(Path.Combine(folder, "catalog"), discoveryProviders: []);
            var projects = new List<ProjectEntry>();
            for (var i = 0; i < 3; i++)
            {
                var source = Path.Combine(folder, "source-" + i); Directory.CreateDirectory(Path.Combine(source, "files"));
                await File.WriteAllTextAsync(Path.Combine(source, "files", "data.txt"), "Captured queue contents " + i);
                projects.Add(app.Catalog.RegisterCandidate("Queued " + i, [source], "Manual", approved: true));
            }
            var destination = await app.Backups.AddDestinationAsync("Queue", Path.Combine(folder, "repository"), protection: protection);
            var runner = new RecordingRunner(); var restic = new ResticClient(app.Paths, app.Credentials, runner, app.Restic.Executable);
            var backend = new BackupService(app.Paths, app.Catalog, app.Credentials, restic, app.Planner, app.Discovery);
            await tests.Run(protection + ": queued projects share verification, honor captured configuration, and release the lease", async () =>
            {
                var partial = SavedSelection.FromProject(projects[0], "Queued folder", [Path.Combine(projects[0].Roots[0].Path, "files")]);
                app.Catalog.SaveSelection(partial);
                var requests = projects.Take(2).Select(p => BackupRequest.Capture(destination, p, null, false, BackupReadMode.Full))
                    .Append(BackupRequest.Capture(destination, projects[0], partial, false, BackupReadMode.Full)).ToArray();
                app.Catalog.SaveProject(projects[0] with { Exclusions = new() { ExcludedDirectories = ["files"] } });
                var progress = new ConcurrentQueue<BackupProgress>(); var queue = new BackupQueueCoordinator(backend);
                queue.ProgressReported += progress.Enqueue;
                queue.Enqueue(requests); await queue.WaitForStopAsync();
                var jobs = requests.Select(r => app.Catalog.Job(r.Id)!).ToList();
                Assert(jobs.All(RunOutcome.IsSuccessful) && jobs.All(j => j.ReadMode == BackupReadMode.Full), Json.Write(jobs));
                Assert(runner.Count("backup") == 3 && runner.Count("check") == 1 && runner.Count("snapshots") == 2
                    && jobs.Count(j => j.Coverage == Coverage.FullProject) == 2 && jobs.Count(j => j.Coverage == Coverage.PartialSelection) == 1,
                    "Queue repeated batch maintenance or confused full and partial requests.");
                Assert(progress.Any(p => p.Stage == "Preparing sources" && p.CurrentFile is not null) && progress.Any(p => p.Stage == "Checking live coverage"), "Real backups did not report inventory and coverage activity.");
                var snapshot = (await app.Restic.SnapshotsAsync(destination)).Single(s => s.Id == jobs[0].SnapshotId);
                var manifest = await app.Restic.ManifestAsync(destination, snapshot);
                Assert(!manifest.Project.Exclusions.ExcludedDirectories.Contains("files"), "Execution replaced captured project configuration.");
                var contents = await app.Restic.RunAsync(destination, ["dump", snapshot.Id, PathSafety.SnapshotPath(Path.Combine(projects[0].Roots[0].Path, "files", "data.txt"))]);
                contents.EnsureSuccess("Read captured queued file");
                Assert(contents.Output.TrimEnd('\r', '\n') == "Captured queue contents 0", "Captured exclusions were ignored.");
                Assert(!Directory.EnumerateFileSystemEntries(app.Paths.JobsDirectory).Any(), "Queue retained staging files.");
                using var released = DestinationLease.Acquire(app.Paths, destination.Path);
            });
            await tests.Run(protection + ": cancel current preserves other queued recovery points", async () =>
            {
                runner.Calls.Clear();
                var requests = projects.Select(p => BackupRequest.Capture(destination, p, null, false, BackupReadMode.Incremental)).ToArray();
                var queue = new BackupQueueCoordinator(backend);
                queue.ProgressReported += progress =>
                {
                    if (progress.RequestId == requests[1].Id && progress.Stage == "Preparing sources") queue.CancelCurrent();
                };
                queue.Enqueue(requests); await queue.WaitForStopAsync();
                var jobs = requests.Select(r => app.Catalog.Job(r.Id)!).ToArray();
                Assert(RunOutcome.IsSuccessful(jobs[0]) && jobs[1].Backup == Outcome.Cancelled && RunOutcome.IsSuccessful(jobs[2]), Json.Write(jobs));
                Assert(runner.Count("backup") == 2 && runner.Count("check") == 2, "Cancellation did not end the segment safely.");
                using var released = DestinationLease.Acquire(app.Paths, destination.Path);
            });
            await tests.Run(protection + ": cancelling queue verification cannot finalize unverified snapshots", async () =>
            {
                runner.Calls.Clear();
                var requests = projects.Take(2).Select(p => BackupRequest.Capture(destination, p, null, false, BackupReadMode.Incremental)).ToArray();
                var queue = new BackupQueueCoordinator(backend);
                queue.ProgressReported += progress => { if (progress.Stage == "Verifying repository") queue.CancelAll(); };
                queue.Enqueue(requests); await queue.WaitForStopAsync();
                var jobs = requests.Select(r => app.Catalog.Job(r.Id)!).ToArray();
                Assert(jobs.All(j => j.Backup == Outcome.Cancelled && j.Verification == Outcome.Cancelled && j.Cleanup == Outcome.NotRun), Json.Write(jobs));
                var snapshots = await app.Restic.SnapshotsAsync(destination);
                Assert(jobs.All(j => snapshots.Any(s => s.Id == j.SnapshotId && !s.Successful && s.Tags.Contains("pending")))
                    && runner.Count("tag") == 0 && runner.Count("forget") == 0, "Cancelled snapshots were finalized or cleanup ran.");
                using var released = DestinationLease.Acquire(app.Paths, destination.Path);
            });
        }
    }

    private sealed class RecordingRunner : ProcessRunner
    {
        public ConcurrentQueue<string[]> Calls { get; } = new();
        public int Count(string command) => Calls.Count(c => c.Contains(command));
        public override Task<ProcessResult> RunStreamAsync(string executable, IEnumerable<string> arguments, Func<Stream, CancellationToken, Task> consume,
            CancellationToken token = default, IReadOnlyDictionary<string, string?>? environment = null, string? secret = null)
        {
            var args = arguments.ToArray(); Calls.Enqueue(args);
            return base.RunStreamAsync(executable, args, consume, token, environment, secret);
        }
        public override Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken token = default,
            string? workingDirectory = null, IReadOnlyDictionary<string, string?>? environment = null, Action<string>? onOutput = null, string? secret = null, bool captureOutput = true)
        {
            var args = arguments.ToArray(); Calls.Enqueue(args);
            return base.RunAsync(executable, args, token, workingDirectory, environment, onOutput, secret, captureOutput);
        }
    }
}
