using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using RepoBackup.Core.Models;
using RepoBackup.Desktop.Infrastructure;
using RepoBackup.Desktop.ViewModels;
using RepoBackup.Desktop.Views;
using static RepoBackup.Tests.ControlledBackupBackend;
using static RepoBackup.Tests.DesktopTestSupport;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class DesktopBackupQueueTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        await tests.Run("Desktop queues immediately, stays interactive, coalesces backup progress, and renders across pages", async () =>
        {
            var backend = new ControlledBackupBackend();
            var model = Model(Path.Combine(root, "interactive"), out var dialogs, backend);
            var a = Project(model, root, "First project", approved: true);
            var b = Project(model, root, "Second project", approved: true);
            var firstDestination = Destination(model, root, "First backup drive");
            var secondDestination = Destination(model, root, "Second backup drive");
            await model.InitializeAsync(discover: false, showSetup: false);
            model.SelectedDestination = firstDestination;
            model.Projects.Single(p => p.Id == a.Id).IsSelected = true;
            var started = Signal(); var copy = Signal(); var copied = Signal(); var finishCopy = Signal(); var verifying = Signal(); var finishVerification = Signal();
            var uiThread = Environment.CurrentManagedThreadId; var workerThread = uiThread;
            var wrongThread = false; var fileUpdates = 0;
            var path = Path.Combine(a.Roots[0].Path, string.Join("\\", Enumerable.Repeat("a-long-folder-name", 12)), "résumé-文件[1].txt");
            IProgress<BackupProgress>? oldReporter = null;
            model.Queue.PropertyChanged += (_, e) => { wrongThread |= Environment.CurrentManagedThreadId != uiThread; if (e.PropertyName == nameof(BackupQueueViewModel.ProgressCurrentFile)) fileUpdates++; };
            backend.OnBackup = async (request, progress, token) =>
            {
                workerThread = Environment.CurrentManagedThreadId;
                if (request.Project.Id != a.Id) return;
                oldReporter = progress;
                progress!.Report(new(request.Name, "Preparing sources", IsIndeterminate: true)); started.TrySetResult();
                await copy.Task.WaitAsync(token);
                for (var i = 0; i < 10000; i++) progress.Report(new(request.Name, "Backing up · live", .45, 45, 450,
                    "file-" + i, TotalBytes: 1000, TotalFiles: 100));
                progress.Report(new(request.Name, "Backing up · live", .45, 45, 450, path, TotalBytes: 1000, TotalFiles: 100)); copied.TrySetResult();
                await finishCopy.Task.WaitAsync(token);
            };
            backend.OnComplete = async (progress, token) =>
            {
                progress!.Report(new("", "Verifying repository", IsIndeterminate: true)); verifying.TrySetResult();
                await finishVerification.Task.WaitAsync(token);
            };
            await WithWindowAsync(model, async window =>
            {
                try
                {
                    await ExecuteAsync(model.BackupSelectedCommand); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await UntilAsync(() => model.Queue.IsRunning);
                    Assert(!model.IsBusy && model.CanChooseBackupInputs && !model.CanEditCatalog && model.BackupSelectedCommand.CanExecute(null)
                        && model.SelectedBackupLabel.StartsWith("Queue") && model.BackupAllLabel == "Queue All Enabled", "Backup blocked input or enqueue command waited for completion.");
                    var projects = Descendants<ProjectsPage>(window).Single();
                    Assert(Descendants<DataGrid>(projects).Single().IsEnabled && !model.VerifyCommand.CanExecute(null), "Project browsing was disabled or conflicting repository work was enabled.");
                    model.Projects.Single(p => p.Id == a.Id).IsSelected = false;
                    model.Projects.Single(p => p.Id == b.Id).IsSelected = true;
                    model.SelectedDestination = secondDestination;
                    await ExecuteAsync(model.BackupSelectedCommand);
                    await ExecuteAsync(model.BackupSelectedCommand);
                    Assert(model.Queue.Items.Count == 2 && model.StatusMessage.Contains("already active or queued")
                        && model.Queue.Items.Single(i => i.Request.Project.Id == a.Id).Request.Destination.Id == firstDestination.Id
                        && model.Queue.Items.Single(i => i.Request.Project.Id == b.Id).Request.Destination.Id == secondDestination.Id, "Queued destinations changed or duplicate was not explained.");
                    model.Search = "Second"; Assert(model.ProjectView.Cast<ProjectItem>().Count() == 1, "Search froze during backup."); model.Search = "";
                    copy.TrySetResult(); await copied.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await UntilAsync(() => model.Queue.ProgressCurrentFile == path);
                    Assert(workerThread != uiThread && !wrongThread && fileUpdates <= 3 && model.Queue.ProgressPercent == 45, "Work ran on UI thread or progress flooded its dispatcher.");
                    var status = Descendants<BackupQueueStatus>(window).Single();
                    var bar = (ProgressBar)status.FindName("Progress"); var file = (TextBlock)status.FindName("FileLine");
                    var pauseButton = (Button)status.FindName("PauseButton");
                    Assert(pauseButton.Focus() && pauseButton.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)), "Keyboard navigation could not reach and leave the queue controls.");
                    Assert(!bar.IsIndeterminate && file.Text.StartsWith("Current file:") && (string)file.ToolTip == path
                        && model.Queue.ProgressSummary.Contains("45 / 100 files") && model.Queue.CurrentDestination == firstDestination.Name, "Backup progress lost counts, filename or active destination.");
                    foreach (var section in new[] { "Projects", "Discoveries", "Snapshots & Restore", "Destinations", "Activity", "Settings" })
                    {
                        model.Section = section; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        Assert(status.IsVisible, "Progress disappeared on " + section);
                    }
                    model.Section = "Activity";
                    foreach (var size in new[] { new Size(1140, 730), new Size(1500, 960) })
                    foreach (var scale in new[] { 1d, 1.5d })
                    {
                        window.Width = size.Width; window.Height = size.Height; window.UpdateLayout();
                        var content = (FrameworkElement)window.Content;
                        var panel = Descendants<BackupQueuePanel>(window).Single();
                        var actions = Descendants<Button>(status).Where(button => button.IsVisible).ToList();
                        Assert(new FrameworkElement[] { status, panel, bar, file }.All(element => element.IsVisible && Inside(element, content))
                            && actions.All(button => Inside(button, content) && button.IsTabStop), "Queue/progress controls were clipped or inaccessible at " + size);
                        Assert(Descendants<Button>(panel).Any(button => Equals(button.Content, "Remove") && button.IsEnabled), "Waiting entry controls were absent.");
                        Render(window, Path.Combine(root, $"queue-{size.Width}x{size.Height}-{scale}.png"), scale);
                    }
                    model.Queue.Pause(); finishCopy.TrySetResult(); await verifying.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await UntilAsync(() => model.Queue.ProgressStage == "Verifying repository");
                    Assert(model.Queue.IsProgressIndeterminate && !model.Queue.HasProgressCurrentFile && !model.Queue.CanCancelCurrent
                        && !model.Queue.IsQuiescent, "Shared verification exposed stale file activity or conflicting actions.");
                    oldReporter!.Report(new("Old project", "Stale activity", .3, CurrentFile: "stale.txt"));
                    await Task.Delay(300); Assert(model.Queue.ProgressStage == "Verifying repository", "A retired project reporter replaced verification.");
                    finishVerification.TrySetResult(); await model.Queue.WaitForStopAsync();
                    await UntilAsync(() => !model.Queue.IsRunning && model.Queue.Items.Count == 1);
                    Assert(model.Queue.IsPaused && model.CanEditCatalog && model.Queue.Items[0].Request.Project.Id == b.Id, "Pause started another project or kept the interface locked.");
                    model.Queue.Resume(); await model.Queue.WaitForStopAsync(); await UntilAsync(() => !model.Queue.HasWork);
                    Assert(!status.IsVisible && backend.Started.Count == 2 && dialogs.Errors.Count == 0, "Queue did not finish cleanly.");
                    oldReporter.Report(new("Old", "Late progress", .4, CurrentFile: "late.txt")); await Task.Delay(300);
                    Assert(!model.Queue.HasProgressCurrentFile && !model.Queue.HasWork, "Late progress resurrected the queue.");
                }
                finally
                {
                    copy.TrySetResult(); finishCopy.TrySetResult(); finishVerification.TrySetResult();
                    model.Queue.CancelAll(); await model.Queue.WaitForStopAsync();
                }
            });
        });

        await tests.Run("Snapshot selection during queued work cannot display the previous snapshot's files", async () =>
        {
            var folder = Path.Combine(root, "snapshot-selection");
            var backend = new ControlledBackupBackend(); var model = Model(folder, out _, backend);
            Project(model, folder, "Snapshot fixture", approved: true); Destination(model, folder);
            await model.InitializeAsync(discover: false, showSetup: false);
            var first = new SnapshotItem(new SnapshotInfo("first", DateTimeOffset.UtcNow, [], []));
            var second = new SnapshotItem(new SnapshotInfo("second", DateTimeOffset.UtcNow, [], []));
            var reads = 0;
            model.SnapshotFileLoader = (_, snapshot, _) =>
            {
                reads++;
                return Task.FromResult(new List<SnapshotFileItem> { new(new("/" + snapshot.Id, "file", 1), snapshot.Id) });
            };
            model.SelectedSnapshot = first; await model.RefreshSnapshotFilesAsync();
            Assert(model.SnapshotFiles.Single().DisplayPath == "first", "Snapshot fixture did not load its first file.");
            var started = Signal();
            backend.OnBackup = async (_, _, token) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); };
            try
            {
                await ExecuteAsync(model.BackupAllCommand); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var previousReads = reads;
                model.SelectedSnapshot = second;
                Assert(model.SnapshotFiles.Count == 0 && model.SelectedSnapshotFile is null && reads == previousReads && model.RepositoryBrowsingDeferred,
                    "Deferred selection retained stale files or started a conflicting repository read.");
            }
            finally { model.Queue.CancelAll(); await model.Queue.WaitForStopAsync(); }
            await model.RefreshSnapshotFilesAsync();
            Assert(model.SnapshotFiles.Single().DisplayPath == "second", "Paused/completed queue did not allow the selected files to load.");
        });
        foreach (var paused in new[] { false, true })
            await tests.Run("Desktop close preserves unfinished work on decline and drains cancellation safely (paused=" + paused + ")", async () =>
            {
                var folder = Path.Combine(root, paused ? "close-paused" : "close-running");
                var backend = new ControlledBackupBackend(); var model = Model(folder, out var dialogs, backend);
                Project(model, folder, "Close fixture", approved: true); Destination(model, folder);
                await model.InitializeAsync(discover: false, showSetup: false);
                var started = Signal();
                backend.OnBackup = async (_, _, token) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); };
                if (paused) model.Queue.Pause();
                await ExecuteAsync(model.BackupAllCommand);
                if (!paused) await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                dialogs.ConfirmResult = false;
                Assert(!await model.PrepareToCloseAsync() && model.Queue.HasUnfinishedWork && !model.IsClosing, "Declining close discarded work.");
                dialogs.ConfirmResult = true;
                Assert(await model.PrepareToCloseAsync() && !model.Queue.HasUnfinishedWork && dialogs.ConfirmCalls == 2, "Close returned before cancellation and resource release.");
                await UntilAsync(() => !model.Queue.HasWork);
                model.Queue.Dispose();
            });
    }
}
