using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RepoBackup.Core.Models;
using RepoBackup.Desktop.Infrastructure;
using RepoBackup.Desktop.ViewModels;
using RepoBackup.Desktop.Views;
using static RepoBackup.Tests.DesktopTestSupport;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

// Runs on the desktop fixture's STA dispatcher with the real window and resources.
public static class DesktopRestoreProgressTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        foreach (var mode in new[] { "entire", "test", "file", "folder" })
            await tests.Run("Desktop " + mode + " restore shows live progress and retires queued file updates", async () =>
            {
                var model = await PrepareAsync(Path.Combine(root, mode), out var dialogs);
                var uiThread = Environment.CurrentManagedThreadId; var workerThread = uiThread;
                var started = Signal(); var copy = Signal(); var copied = Signal(); var verify = Signal(); var verified = Signal(); var finish = Signal();
                var latestFile = "source\\" + string.Join("\\", Enumerable.Repeat("a-long-folder-name", 12)) + "\\résumé-文件[1].txt";
                IProgress<BackupProgress>? reporter = null;
                string? selected = null, restoreTarget = null;
                var fileChanges = 0; var wrongThread = false;
                model.PropertyChanged += (_, e) =>
                {
                    wrongThread |= Environment.CurrentManagedThreadId != uiThread;
                    if (e.PropertyName == nameof(model.ProgressCurrentFile)) fileChanges++;
                };
                model.RestoreExecutor = async (_, _, target, path, progress, token) =>
                {
                    workerThread = Environment.CurrentManagedThreadId; selected = path; restoreTarget = target; reporter = progress;
                    progress.Report(new("Project", "Preparing restore", IsIndeterminate: true)); started.TrySetResult();
                    await copy.Task.WaitAsync(token);
                    progress.Report(new("Project", "Restoring source", .25, Bytes: 250, CurrentFile: "source\\first.txt", TotalBytes: 1000));
                    for (var i = 0; i < 2000; i++) progress.Report(new("Project", "Restoring source", .4, Bytes: 400, CurrentFile: "source\\file-" + i, TotalBytes: 1000));
                    progress.Report(new("Project", "Restoring source", .4, Bytes: 400, CurrentFile: latestFile, TotalBytes: 1000)); copied.TrySetResult();
                    await verify.Task.WaitAsync(token);
                    progress.Report(new("Project", "Verifying source", .99, Bytes: 1000, IsIndeterminate: true, TotalBytes: 1000)); verified.TrySetResult();
                    await finish.Task.WaitAsync(token);
                    progress.Report(new("Project", "Restore completed", 1, Bytes: 1000, TotalBytes: 1000));
                    return target;
                };
                dialogs.OnInformation = message =>
                {
                    Assert(model.ProgressPercent == 100 && !model.HasProgressCurrentFile, "Success dialog opened before final progress was applied.");
                    reporter!.Report(new("Project", "Stale progress", .3, CurrentFile: "stale.txt"));
                    var frame = new DispatcherFrame();
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
                    Dispatcher.PushFrame(frame);
                    Assert(model.StatusMessage == message && model.ProgressPercent == 100 && !model.HasProgressCurrentFile, "Queued activity overwrote the success dialog.");
                };
                if (mode is "file" or "folder") model.SelectedSnapshotFile = new(new("/source/selected", mode == "file" ? "file" : "dir", 10), "Selected item");
                var command = (AsyncCommand)(mode == "test" ? model.TestRestoreCommand : mode is "file" or "folder" ? model.RestoreFileCommand : model.RestoreCommand);
                await WithWindowAsync(model, async window =>
                {
                    var control = Descendants<OperationStatus>(window).Single();
                    var bar = (ProgressBar)control.FindName("Progress");
                    var fileLine = (TextBlock)control.FindName("FileLine");
                    var summary = (TextBlock)control.FindName("SummaryLine");
                    var cancel = (Button)control.FindName("CancelButton");
                    try
                    {
                        command.Execute(null); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        await UntilAsync(() => model.ProgressStage.Contains("Preparing restore"));
                        Assert(model.IsBusy && bar.IsVisible && bar.IsIndeterminate && !fileLine.IsVisible && cancel.IsEnabled, "Preparation did not render animated progress.");
                        copy.TrySetResult(); await copied.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        await UntilAsync(() => model.ProgressCurrentFile == latestFile);
                        window.UpdateLayout();
                        Assert(workerThread != uiThread && !wrongThread, "Restore work or progress notifications ran on the wrong thread.");
                        Assert(fileChanges <= 3 && bar.Value == 40 && !bar.IsIndeterminate && summary.Text.Contains("40%") && summary.Text.Contains("400 B of 1000 B"), "File burst flooded the UI or lost byte progress.");
                        Assert(fileLine.Text.Contains("Latest restored file:") && (string)fileLine.ToolTip == latestFile && fileLine.TextTrimming == TextTrimming.CharacterEllipsis, "Latest filename or full-path tooltip was missing.");
                        Assert(selected == (mode is "file" or "folder" ? "/source/selected" : null), "Wrong selection reached the restore service.");
                        Assert(Path.GetFileName(restoreTarget!).StartsWith(mode == "test" ? "TestRestore-" : "Restored-"), "Wrong restore target prefix.");
                        if (mode == "entire")
                            foreach (var size in new[] { new Size(1140, 730), new Size(1500, 960) })
                            foreach (var scale in new[] { 1d, 1.5d })
                            {
                                window.Width = size.Width; window.Height = size.Height; window.UpdateLayout();
                                var content = (FrameworkElement)window.Content;
                                Assert(new FrameworkElement[] { control, bar, fileLine, summary, cancel }.All(e => e.IsVisible && Inside(e, content)), "Restore activity was clipped at " + size);
                                var actions = Descendants<Button>(window).Where(b => b.IsVisible && b.Content is string text && (text.StartsWith("Restore ") || text == "Test restore")).ToList();
                                Assert(actions.Count == 3 && actions.All(b => Inside(b, content)), "Restore actions were clipped by the progress area.");
                                Render(window, Path.Combine(root, $"restore-progress-{size.Width}x{size.Height}-{scale}.png"), scale);
                            }
                        verify.TrySetResult(); await verified.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        await UntilAsync(() => model.ProgressStage.Contains("Verifying source"));
                        Assert(bar.IsIndeterminate && !fileLine.IsVisible && model.ProgressPercent < 100 && !summary.Text.Contains("100%"), "Verification looked complete or retained file activity.");
                        finish.TrySetResult(); await command.Execution.WaitAsync(TimeSpan.FromSeconds(5));
                        Assert(!model.IsBusy && !control.IsVisible && !model.HasProgressCurrentFile && dialogs.Errors.Count == 0 && dialogs.InformationMessages.Count == 1, "Restore did not clean up successfully.");
                    }
                    finally
                    {
                        copy.TrySetResult(); verify.TrySetResult(); finish.TrySetResult(); model.Cancel();
                        await command.Execution.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                });
            });

        foreach (var cancelOperation in new[] { false, true })
            await tests.Run(cancelOperation ? "Desktop restore cancellation remains responsive and clears stale activity" : "Desktop verification failure cannot show 100% or accept late progress", async () =>
            {
                var model = await PrepareAsync(Path.Combine(root, cancelOperation ? "cancel" : "failure"), out var dialogs);
                var started = Signal(); var proceed = Signal(); var verifying = Signal(); var end = Signal();
                IProgress<BackupProgress>? oldReporter = null;
                model.RestoreExecutor = async (_, _, target, _, progress, token) =>
                {
                    oldReporter = progress;
                    progress.Report(new("Project", "Restoring source", .4, Bytes: 400, CurrentFile: "source\\file.txt", TotalBytes: 1000)); started.TrySetResult();
                    await proceed.Task.WaitAsync(token);
                    progress.Report(new("Project", "Verifying source", .99, Bytes: 1000, IsIndeterminate: true, TotalBytes: 1000)); verifying.TrySetResult();
                    await end.Task.WaitAsync(token);
                    throw new IOException("Injected verification failure.");
                };
                var command = (AsyncCommand)model.TestRestoreCommand;
                await WithWindowAsync(model, async window =>
                {
                    try
                    {
                        command.Execute(null); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        await UntilAsync(() => model.HasProgressCurrentFile);
                        if (cancelOperation) model.CancelCommand.Execute(null);
                        else
                        {
                            proceed.TrySetResult(); await verifying.Task.WaitAsync(TimeSpan.FromSeconds(5));
                            await UntilAsync(() => model.IsProgressIndeterminate && !model.HasProgressCurrentFile);
                            end.TrySetResult();
                        }
                        await command.Execution.WaitAsync(TimeSpan.FromSeconds(5));
                        Assert(!model.IsBusy && !model.HasProgressCurrentFile && model.ProgressPercent < 100 && dialogs.InformationMessages.Count == 0
                            && dialogs.Errors.Count == (cancelOperation ? 0 : 1), "Failure or cancellation claimed success.");
                        var stage = model.ProgressStage; var status = model.StatusMessage;
                        oldReporter!.Report(new("Project", "Late file", 1, CurrentFile: "late.txt"));
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        Assert(model.ProgressStage == stage && model.StatusMessage == status && !model.HasProgressCurrentFile, "Late callback changed the terminal result.");
                        if (cancelOperation) Assert(status == "Operation cancelled.", "Cancellation status was lost.");
                        // A reporter from the previous operation must also be ignored during a new one.
                        var nextStarted = Signal(); var nextFinish = Signal();
                        model.RestoreExecutor = async (_, _, target, _, progress, token) =>
                        {
                            progress.Report(new("Project", "Preparing next restore", IsIndeterminate: true)); nextStarted.TrySetResult();
                            await nextFinish.Task.WaitAsync(token); progress.Report(new("Project", "Restore completed", 1)); return target;
                        };
                        await model.RefreshSnapshotsAsync();
                        Assert(command.CanExecute(null), "Next restore was disabled after refreshing recovery points.");
                        command.Execute(null); await nextStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        await UntilAsync(() => model.ProgressStage.Contains("Preparing next restore"));
                        oldReporter.Report(new("Project", "Previous operation", .8, CurrentFile: "old.txt"));
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        Assert(model.ProgressPercent == 0 && model.IsProgressIndeterminate && !model.HasProgressCurrentFile, "New restore inherited previous activity.");
                        nextFinish.TrySetResult(); await command.Execution.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    finally { proceed.TrySetResult(); end.TrySetResult(); model.Cancel(); await command.Execution.WaitAsync(TimeSpan.FromSeconds(5)); }
                });
            });

        await tests.Run("Dispatcher progress coalesces file bursts and immediately delivers phase transitions", async () =>
        {
            var applied = new List<BackupProgress>(); using var progress = new DispatcherProgress(applied.Add);
            progress.Report(new("Project", "Restoring")); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); applied.Clear();
            await Task.Run(() => { for (var i = 0; i < 10000; i++) progress.Report(new("Project", "Restoring", .5, CurrentFile: "file-" + i)); });
            await UntilAsync(() => applied.Any(p => p.CurrentFile == "file-9999"));
            Assert(applied.Count <= 2, "Every file event queued a dispatcher callback.");
            progress.Report(new("Project", "Verifying", .99, IsIndeterminate: true));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert(applied[^1].Stage == "Verifying", "Phase transition waited for the timer.");
            progress.Report(new("Project", "Restore completed", 1)); progress.Complete(); var count = applied.Count;
            progress.Report(new("Project", "Late", CurrentFile: "late.txt")); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert(applied.Count == count && applied[^1].Fraction == 1, "Retired reporter accepted a queued or late update.");
        });
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private static Task<MainViewModel> PrepareAsync(string root, out TestDialogs dialogs)
    {
        var model = Model(root, out dialogs); dialogs.FolderPath = root;
        Project(model, root, "Project", approved: true); Destination(model, root);
        var snapshot = new SnapshotInfo(new string('a', 64), DateTimeOffset.UtcNow, ["successful"], []);
        model.SnapshotLoader = (_, _) => Task.FromResult(new List<SnapshotInfo> { snapshot });
        return LoadAsync();
        async Task<MainViewModel> LoadAsync()
        {
            await model.ReloadAsync(); model.Section = "Snapshots & Restore"; await model.RefreshSnapshotsAsync(); return model;
        }
    }
}
