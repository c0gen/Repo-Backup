using RepoBackup.Core.Models;
using RepoBackup.Desktop.Infrastructure;
using RepoBackup.Desktop.ViewModels;
using static RepoBackup.Tests.ControlledBackupBackend;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class BackupQueueTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        await tests.Run("Queue captures configuration without sharing mutable project or selection lists", () =>
        {
            var project = new ProjectEntry { Name = "Captured", Roots = [new("r", Path.Combine(root, "source"))] };
            var selection = SavedSelection.FromProject(project, "Folder", [project.Roots[0].Path]);
            var request = BackupRequest.Capture(Destination(root), project, selection, true, BackupReadMode.Full);
            project.Exclusions.ExcludedDirectories.Clear(); project.Roots[0].PreviousPaths.Add("changed"); project.Roots.Clear();
            selection.Paths.Clear(); selection.Exclusions.RelativePatterns.Add("changed");
            Assert(request.Project.Roots.Count == 1 && request.Project.Roots[0].PreviousPaths.Count == 0
                && request.Project.Exclusions.ExcludedDirectories.Count > 0 && request.Selection!.Paths.Count == 1
                && request.Selection.Exclusions.RelativePatterns.Count == 0 && request.UseVss && request.ReadMode == BackupReadMode.Full, "Queued configuration changed after capture.");
            return Task.CompletedTask;
        });
        await tests.Run("Queue appends while active, suppresses duplicates, and batches consecutive destinations", async () =>
        {
            var backend = new ControlledBackupBackend(); var release = Signal();
            var a = Request(root, "A"); var b = Request(root, "B", a.Destination);
            var c = Request(root, "C"); var d = Request(root, "D", a.Destination);
            backend.OnBackup = async (request, _, token) => { if (request.Id == a.Id) await release.Task.WaitAsync(token); };
            var queue = new BackupQueueCoordinator(backend);
            queue.Enqueue([a, b]); await UntilAsync(() => backend.Started.Count == 1);
            var added = queue.Enqueue([a, b, c, d]);
            Assert(added == (2, 2), "Duplicate active or waiting scope was accepted.");
            release.SetResult(); await queue.WaitForStopAsync();
            Assert(backend.Started.Select(r => r.Name).SequenceEqual(["A", "B", "C", "D"]), "Queue order changed.");
            Assert(backend.OpenCount == 3 && backend.CompleteCount == 3 && backend.MaximumSessions == 1, "Destination runs overlapped or were not batched.");
            Assert(!queue.Snapshot.IsRunning && queue.Snapshot.Entries.Count == 0 && backend.Results.Values.All(j => j.Backup == Outcome.Successful), "Queue did not settle.");
        });
        await tests.Run("Paused queue supports removal and reordering, then pauses after current verification", async () =>
        {
            var backend = new ControlledBackupBackend(); var release = Signal();
            var a = Request(root, "A"); var b = Request(root, "B", a.Destination); var c = Request(root, "C", a.Destination); var d = Request(root, "D", a.Destination);
            backend.OnBackup = async (request, _, token) => { if (request.Id == a.Id) await release.Task.WaitAsync(token); };
            var queue = new BackupQueueCoordinator(backend); queue.Pause(); queue.Enqueue([a, b, c, d]);
            Assert(!queue.Snapshot.IsRunning && backend.Started.IsEmpty, "Paused enqueue started work.");
            queue.Move(d.Id, -1); queue.Move(d.Id, -1); queue.Remove(b.Id);
            Assert(queue.Snapshot.Entries.Select(e => e.Request.Name).SequenceEqual(["A", "D", "C"]), "Waiting order/removal failed.");
            queue.Resume(); await UntilAsync(() => backend.Started.Count == 1); queue.Pause();
            Assert(!queue.CanMove(a.Id, 1), "Active request could be moved.");
            release.SetResult(); await queue.WaitForStopAsync();
            Assert(queue.Snapshot.IsPaused && queue.Snapshot.Entries.Count == 2 && backend.Results[a.Id].Backup == Outcome.Successful && backend.CompleteCount == 1, "Pause skipped finalization or consumed another project.");
            queue.Resume(); await queue.WaitForStopAsync();
            Assert(backend.Started.Select(r => r.Name).SequenceEqual(["A", "D", "C"]) && backend.CompleteCount == 2, "Resume lost the chosen order.");
        });
        await tests.Run("Cancel current preserves earlier pending snapshots and continues waiting requests", async () =>
        {
            var backend = new ControlledBackupBackend(); var started = Signal();
            var a = Request(root, "A"); var b = Request(root, "B", a.Destination); var c = Request(root, "C", a.Destination);
            backend.OnBackup = async (request, _, token) => { if (request.Id == b.Id) { started.SetResult(); await Task.Delay(Timeout.Infinite, token); } };
            var queue = new BackupQueueCoordinator(backend); queue.Enqueue([a, b, c]); await started.Task;
            Assert(queue.Snapshot.Entries.Any(e => e.Request.Id == a.Id && e.State == BackupQueueState.AwaitingVerification), "Earlier project was prematurely successful.");
            queue.CancelCurrent(a.Id);
            Assert(queue.Snapshot.CurrentRequestId == b.Id && queue.Snapshot.CanCancelCurrent, "A delayed cancel for the previous project cancelled its successor.");
            queue.CancelCurrent(b.Id); await queue.WaitForStopAsync();
            Assert(backend.Results[a.Id].Backup == Outcome.Successful && backend.Results[b.Id].Backup == Outcome.Cancelled && backend.Results[c.Id].Backup == Outcome.Successful
                && backend.CompleteCount == 2 && backend.Started.Count == 3, "Cancel current leaked into other requests.");
        });
        await tests.Run("Cancel all interrupts shared verification and discards waiting requests", async () =>
        {
            var backend = new ControlledBackupBackend(); var verifying = Signal();
            var a = Request(root, "A"); var b = Request(root, "B");
            backend.OnComplete = async (_, token) => { verifying.SetResult(); await Task.Delay(Timeout.Infinite, token); };
            var queue = new BackupQueueCoordinator(backend); queue.Enqueue([a, b]); await verifying.Task;
            Assert(!queue.Snapshot.CanCancelCurrent && queue.Snapshot.IsFinalizing, "Shared verification exposed project cancellation.");
            queue.CancelAll(); await queue.WaitForStopAsync();
            Assert(backend.Results[a.Id].Backup == Outcome.Cancelled && !backend.Results.ContainsKey(b.Id) && backend.Started.Count == 1
                && !queue.Snapshot.IsRunning && queue.Snapshot.Entries.Count == 0, "Cancel all started waiting work or stranded the queue.");
        });
        await tests.Run("Preparation and project failures remain terminal while other requests continue", async () =>
        {
            var backend = new ControlledBackupBackend(); var a = Request(root, "A"); var b = Request(root, "B"); var c = Request(root, "C", b.Destination);
            backend.OnOpen = (destination, _) => destination == a.Destination ? Task.FromException(new IOException("Unavailable destination")) : Task.CompletedTask;
            backend.OnBackup = (request, _, _) => request.Id == b.Id ? Task.FromException(new IOException("Unreadable source")) : Task.CompletedTask;
            var queue = new BackupQueueCoordinator(backend); queue.Enqueue([a, b, c]); await queue.WaitForStopAsync();
            Assert(backend.Results[a.Id].Backup == Outcome.Failed && backend.Results[b.Id].Backup == Outcome.Failed && backend.Results[c.Id].Backup == Outcome.Successful
                && backend.OpenCount == 2 && backend.Started.Count == 2, "Failure stopped the queue or was retried.");
        });
        await tests.Run("Shared progress reporters cannot claim ownership of a later destination run", async () =>
        {
            var backend = new ControlledBackupBackend(); var second = Signal(); var release = Signal();
            IProgress<BackupProgress>? retired = null;
            var calls = 0;
            backend.OnComplete = async (progress, token) =>
            {
                if (Interlocked.Increment(ref calls) == 1) { retired = progress; return; }
                second.TrySetResult(); await release.Task.WaitAsync(token);
            };
            var queue = new BackupQueueCoordinator(backend);
            BackupProgress? latest = null;
            queue.ProgressReported += progress => latest = progress;
            queue.Enqueue([Request(root, "A"), Request(root, "B")]);
            await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                retired!.Report(new("", "Retired finalization", IsIndeterminate: true));
                Assert(latest is not null && !queue.Accepts(latest), "Late shared telemetry acquired a newer run's identity.");
            }
            finally { release.TrySetResult(); await queue.WaitForStopAsync(); }
        });
        await tests.Run("Concurrent enqueue and worker completion never create overlapping sessions or lose work", async () =>
        {
            var backend = new ControlledBackupBackend(); var queue = new BackupQueueCoordinator(backend);
            var requests = Enumerable.Range(0, 40).Select(i => Request(root, "Project " + i)).ToArray();
            await Task.WhenAll(requests.Select(request => Task.Run(() => queue.Enqueue([request]))));
            await queue.WaitForStopAsync();
            Assert(backend.Started.Select(r => r.Id).Distinct().Count() == requests.Length && backend.Results.Count == requests.Length
                && backend.MaximumSessions == 1 && queue.Snapshot.Entries.Count == 0, "Work was lost or workers overlapped at completion.");
        });
    }

    private static Destination Destination(string root) => new() { Name = "Backup", Path = Path.Combine(root, Guid.NewGuid().ToString("N")), RepositoryId = "fixture" };
    internal static BackupRequest Request(string root, string name, Destination? destination = null) => BackupRequest.Capture(destination ?? Destination(root),
        new ProjectEntry { Name = name, Reviewed = true, Roots = [new("r", Path.Combine(root, name))] }, null, false, BackupReadMode.Incremental);
}
