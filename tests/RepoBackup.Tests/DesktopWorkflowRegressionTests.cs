using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RepoBackup.Core.Application;
using RepoBackup.Core.Backup;
using RepoBackup.Core.Models;
using RepoBackup.Desktop;
using RepoBackup.Desktop.Infrastructure;
using RepoBackup.Desktop.ViewModels;
using RepoBackup.Desktop.Views;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class DesktopWorkflowRegressionTests
{
    public static bool IncludeScheduling { get; set; }
    // Called on the existing WPF fixture dispatcher, sharing its Application resources.
    public static async Task RunAsync(TestRunner tests, string root)
    {
        Directory.CreateDirectory(root);
        var runner = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "src", "RepoBackup.Runner", "bin", "Release", "net10.0-windows", "RepoBackup.Runner.exe"));
        var app = new ApplicationServices(Path.Combine(root, "catalog"), discoveryProviders: [], schedulerRunnerPath: runner);
        var paths = new[] { Path.Combine(root, "Alpha"), Path.Combine(root, "Beta") };
        foreach (var path in paths) { Directory.CreateDirectory(path); await File.WriteAllTextAsync(Path.Combine(path, "file.txt"), "Workflow fixture"); }
        var alpha = app.Catalog.RegisterCandidate("Alpha", [paths[0]], "Manual", approved: true);
        var beta = app.Catalog.RegisterCandidate("Beta", [paths[1]], "Manual", approved: true);
        var scope = new SavedSelection { ProjectId = alpha.Id, Name = "Alpha file", Paths = [Path.Combine(paths[0], "file.txt")] }; app.Catalog.SaveSelection(scope);
        var first = new Destination { Name = "Protected drive", Path = Path.Combine(root, "first"), RepositoryId = new string('a', 64) };
        var second = new Destination { Name = "Empty drive", Path = Path.Combine(root, "second"), RepositoryId = new string('b', 64) };
        app.Catalog.SaveDestination(first); app.Catalog.SaveDestination(second);
        var full = new JobRecord { ProjectId = alpha.Id, ProjectName = alpha.Name, SeriesId = "project-" + alpha.Id, DestinationId = first.Id, Backup = Outcome.Successful, Verification = Outcome.Successful, Cleanup = Outcome.Successful, SnapshotId = new string('c', 64), FinishedAt = DateTimeOffset.UtcNow };
        app.Catalog.SaveJob(full);
        var dialogs = new FixtureDialogs(); var model = new MainViewModel(app, dialogs); await model.ReloadAsync();
        await tests.Run("Desktop protection and Needs backup refresh for the chosen destination", async () =>
        {
            model.SelectedDestination = first;
            Assert(model.Projects.Single(p => p.Id == alpha.Id).Status == "Ready", "First destination lost full-project protection.");
            model.ProjectFilter = "Needs backup"; Assert(model.ProjectView.Cast<ProjectItem>().Count() == 1, "Protected project needs backup.");
            model.SelectedDestination = second;
            Assert(model.Projects.All(p => p.LastBackup is null && p.NeedsBackup) && model.ProjectView.Cast<ProjectItem>().Count() == 2, "Empty destination inherited protection.");
            app.Catalog.SaveJob(full with { Id = Guid.NewGuid().ToString("N"), StartedAt = full.StartedAt.AddMinutes(1), Backup = Outcome.Cancelled });
            await model.ReloadAsync();
            model.SelectedDestination = first;
            var item = model.Projects.Single(p => p.Id == alpha.Id);
            Assert(item.LastBackup is not null && item.NeedsBackup && item.Status == "Cancelled", "Later unsuccessful full attempt did not require a backup.");
            var protection = ProjectProtection.For(alpha.Id, second.Id, [full with { DestinationId = second.Id, Coverage = Coverage.PartialSelection }]);
            Assert(protection.NeedsBackup, "Partial selection counted as full protection.");
            model.ProjectFilter = "All projects";
        });
        await tests.Run("Select all visible toggles twice and preserves explicitly counted hidden selections", () =>
        {
            model.Projects.Single(p => p.Id == beta.Id).IsSelected = true; model.Search = "Alpha";
            model.ToggleAllCommand.Execute(null); Assert(model.SelectedCount == 2 && model.SelectedSummary.Contains("1 hidden"), "Hidden selected count missing.");
            model.ToggleAllCommand.Execute(null); Assert(model.SelectedCount == 1 && model.Projects.Single(p => p.Id == beta.Id).IsSelected && !model.Projects.Single(p => p.Id == alpha.Id).IsSelected, "Second click did not clear only visible rows.");
            model.Search = ""; return Task.CompletedTask;
        });
        await tests.Run("Delayed snapshots cannot repopulate an empty destination and superseded requests are cancelled", async () =>
        {
            var pending = new TaskCompletionSource<List<SnapshotInfo>>(); CancellationToken firstToken = default;
            model.SnapshotLoader = (d, token) => { if (d.Id == first.Id) { firstToken = token; return pending.Task; } return Task.FromResult(new List<SnapshotInfo>()); };
            model.SelectedDestination = first; var load = model.RefreshSnapshotsAsync(); model.SelectedDestination = second;
            await model.RefreshSnapshotsAsync();
            pending.SetResult([new SnapshotInfo(new string('d', 64), DateTimeOffset.UtcNow, [], [])]); await load;
            Assert(firstToken.IsCancellationRequested && model.Snapshots.Count == 0, "A stale destination's snapshots became visible.");
        });
        await tests.Run("Delayed files reject changed snapshot and destination identities", async () =>
        {
            var pending = new TaskCompletionSource<List<SnapshotFileItem>>(); CancellationToken oldToken = default;
            var old = new SnapshotItem(new(new string('e', 64), DateTimeOffset.UtcNow, [], []));
            model.SnapshotFileLoader = (d, s, token) => { if (s.Id == old.Snapshot.Id) { oldToken = token; return pending.Task; } return Task.FromResult(new List<SnapshotFileItem>()); };
            model.SelectedSnapshot = old;
            model.SelectedSnapshot = new(new(new string('f', 64), DateTimeOffset.UtcNow, [], []));
            pending.SetResult([new(new("/wrong.txt", "file", 1), "wrong.txt")]);
            await Dispatcher.Yield(DispatcherPriority.Background);
            Assert(oldToken.IsCancellationRequested && model.SnapshotFiles.Count == 0 && model.SelectedSnapshotFile is null, "Superseded snapshot files became visible.");
            var destinationPending = new TaskCompletionSource<List<SnapshotFileItem>>();
            model.SnapshotFileLoader = (_, _, _) => destinationPending.Task;
            model.SelectedSnapshot = old; model.SelectedDestination = first;
            destinationPending.SetResult([new(new("/other-drive.txt", "file", 1), "other-drive.txt")]);
            await Dispatcher.Yield(DispatcherPriority.Background);
            Assert(model.SnapshotFiles.Count == 0, "Changed destination accepted old files.");
            model.SnapshotFileLoader = (_, _, _) => Task.FromResult(new List<SnapshotFileItem>());
        });
        await tests.Run("Repository verification failures remain displayed after their dialog is dismissed", async () =>
        {
            var verification = await app.Backups.VerifyAsync(first); Assert(verification.Outcome == Outcome.Failed, "Missing repository did not fail verification.");
            await model.ReloadAsync();
            Assert(model.LatestVerificationText.Contains("Failed") && model.LatestVerificationText.Contains(verification.Message), "Failure only existed in the transient dialog.");
            model.SelectedDestination = second; Assert(!model.LatestVerificationText.Contains(verification.Message), "Verification outcome leaked across destinations.");
        });
        await tests.Run("Dismissed projects can be reactivated from their desktop view", async () =>
        {
            model.DismissCommand.Execute(model.Projects.Single(p => p.Id == beta.Id)); await ((AsyncCommand)model.DismissCommand).Execution;
            var dismissed = model.DismissedProjects.Single(p => p.Id == beta.Id);
            model.ApproveCommand.Execute(dismissed); await ((AsyncCommand)model.ApproveCommand).Execution;
            Assert(model.DismissedProjects.Count == 0 && model.Projects.Single(p => p.Id == beta.Id).Enabled, "Dismissed project was not reactivated.");
        });
        await tests.Run("Source previews expose all warnings beyond the first four", async () =>
        {
            try
            {
                var missing = Enumerable.Range(1, 8).Select(i => new SourceRoot(Guid.NewGuid().ToString("N"), Path.Combine(root, "missing-source-" + i))).ToList();
                app.Catalog.SaveProject(alpha with { Roots = [.. alpha.Roots, .. missing] });
                var warningModel = new MainViewModel(app, dialogs); await warningModel.ReloadAsync(); await warningModel.RefreshPreviewAsync();
                Assert(missing.All(r => warningModel.PreviewWarnings.Contains(r.Path)), "Some source warnings were truncated.");
            }
            finally { app.Catalog.SaveProject(alpha); }
        });
        await tests.Run("Schedule editor owns destination, applicable frequency validation and repeated-save identity", async () =>
        {
            var editor = model.ScheduleEditor; model.SelectedDestination = first;
            editor.New(second.Id); editor.Name = "Independent schedule"; editor.Hours = "unused invalid interval"; editor.Day = (DayOfWeek)99;
            var created = editor.Build(); editor.Saved(created); editor.Name = "Updated name"; var updated = editor.Build();
            Assert(created.Id == updated.Id && updated.Name == "Updated name" && editor.Mode == "Edit schedule" && model.SelectedDestination.Id == first.Id && updated.DestinationId == second.Id, "Save duplicated a schedule or changed global destination.");
            editor.Kind = ScheduleKind.Interval; await Throws<ArgumentException>(() => { editor.Build(); return Task.CompletedTask; });
            editor.Hours = "2"; Assert(editor.Build().IntervalHours == 2, "Interval validation failed.");
            editor.Kind = ScheduleKind.Weekly; await Throws<ArgumentException>(() => { editor.Build(); return Task.CompletedTask; });
            editor.Load(created); Assert(editor.DestinationId == second.Id && model.SelectedDestination.Id == first.Id, "Editing changed backup destination.");
            model.NewScheduleCommand.Execute(null); Assert(editor.Id is null && editor.Mode == "New schedule", "New command retained the old schedule identity.");
        });
        if (IncludeScheduling)
        {
            await tests.Run("Repeated desktop schedule saves update the same temporary Windows task and preserve the backup destination", async () =>
            {
                model.SelectedDestination = first; model.NewScheduleCommand.Execute(null);
                var page = new SettingsPage { DataContext = model }; page.Measure(new Size(850, 600)); page.Arrange(new Rect(0, 0, 850, 600)); page.UpdateLayout();
                model.ScheduleEditor.DestinationId = second.Id; model.ScheduleEditor.SelectionId = scope.Id; model.ScheduleEditor.Name = "Temporary desktop regression";
                string? savedId = null;
                try
                {
                    model.SaveScheduleCommand.Execute(null);
                    Assert(model.IsBusy && model.ProgressStage == "Saving schedule…" && model.IsProgressIndeterminate, "Schedule save did not report its active operation.");
                    await FinishAsync(model.SaveScheduleCommand);
                    savedId = model.ScheduleEditor.Id;
                    Assert(savedId is not null && model.SelectedSchedule?.Id == savedId && model.ScheduleEditor.Mode == "Edit schedule", "Creation did not retain editor identity. " + string.Join("; ", dialogs.Errors.Select(e => e.Message)));
                    model.ScheduleEditor.Name = "Updated temporary schedule";
                    model.SaveScheduleCommand.Execute(null); await FinishAsync(model.SaveScheduleCommand);
                    page.UpdateLayout();
                    Assert(app.Catalog.Schedules().Count == 1 && app.Catalog.Schedules().Single().Id == savedId && app.Catalog.Schedules().Single().Name == "Updated temporary schedule" && app.Catalog.Schedules().Single().DestinationId == second.Id && app.Catalog.Schedules().Single().SelectionId == scope.Id && model.ScheduleEditor.DestinationId == second.Id && model.ScheduleEditor.SelectionId == scope.Id && model.SelectedDestination.Id == first.Id, "Repeated save duplicated the task or changed destination/scope.");
                    var query = await new Core.Infrastructure.ProcessRunner().RunAsync("schtasks.exe", ["/Query", "/TN", "RepoBackup-" + savedId, "/XML"]);
                    query.EnsureSuccess("Inspect temporary task"); Assert(query.Output.Contains("Updated temporary schedule"), "Windows task was not updated.");
                    model.DeleteScheduleCommand.Execute(null);
                    Assert(model.IsBusy && model.ProgressStage == "Removing schedule…", "Schedule removal did not report its active operation.");
                    await ((AsyncCommand)model.DeleteScheduleCommand).Execution;
                    Assert(app.Catalog.Schedules().Count == 0 && model.SelectedSchedule is null && !model.IsBusy, "Desktop schedule removal did not settle.");
                }
                finally { if (savedId is not null) await app.Scheduler.DeleteAsync(savedId); }
            });
            await tests.Run("Imported schedules without a Windows task can be removed", async () =>
            {
                var imported = new ScheduleDefinition { DestinationId = second.Id, Name = "Imported task missing locally" };
                app.Catalog.SaveSchedules([imported]);
                await app.Scheduler.DeleteAsync(imported.Id);
                Assert(app.Catalog.Schedules().Count == 0, "Missing Windows task prevented catalog removal.");
            });
        }
        await tests.Run("Desktop layouts render at minimum, normal and increased scaling with primary controls visible", async () =>
        {
            model.Search = ""; model.ProjectFilter = "All projects"; model.Projects.First().IsSelected = true;
            foreach (var size in new[] { new Size(1140, 730), new Size(1500, 960) })
            foreach (var scale in new[] { 1d, 1.5d })
            {
                var window = new MainWindow(model) { Width = size.Width, Height = size.Height, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
                try
                {
                    window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    foreach (var section in new[] { "Projects", "Settings", "Destinations", "Discoveries", "Snapshots & Restore", "Activity" })
                    {
                        model.Section = section; if (section == "Settings") await model.RefreshSchedulesAsync(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                        var content = (FrameworkElement)window.Content;
                        var bitmap = new RenderTargetBitmap((int)(size.Width * scale), (int)(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32); bitmap.Render(content);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var output = File.Create(Path.Combine(root, $"{section.Replace(' ', '-').Replace('&', '-')}-{size.Width}x{size.Height}-{scale}.png")); encoder.Save(output);
                        var buttons = Descendants<Button>(content).Where(b => b.IsVisible && b.Content is string text && text.StartsWith("Back Up")).ToList();
                        Assert(buttons.Count == 2 && buttons.All(b => Inside(b, content)), "Primary actions clipped at " + size);
                        if (section == "Projects")
                        {
                            var grid = Descendants<DataGrid>(content).First(g => g.IsVisible);
                            Assert(grid.Columns.Sum(c => c.ActualWidth) <= grid.ActualWidth + 1, "Project status requires horizontal scrolling at " + size);
                        }
                    }
                    model.Section = "Projects"; window.UpdateLayout();
                    var firstButton = Descendants<Button>((FrameworkElement)window.Content).First(b => b.IsVisible && b.IsEnabled && b.Focusable);
                    Assert(firstButton.Focus(), "Keyboard focus could not reach navigation.");
                    var reached = new HashSet<DependencyObject>();
                    for (var step = 0; step < 100 && Keyboard.FocusedElement is UIElement focused; step++)
                    {
                        reached.Add(focused);
                        if (!focused.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next))) break;
                    }
                    var backupButtons = Descendants<Button>((FrameworkElement)window.Content).Where(b => b.IsVisible && b.Content is string text && text.StartsWith("Back Up")).ToList();
                    Assert(backupButtons.All(reached.Contains), "Tab navigation cannot reach both primary backup actions.");
                    Assert(Descendants<ComboBox>((FrameworkElement)window.Content).Any(c => c.IsVisible && c.ItemsSource == model.Destinations && reached.Contains(c)), "Tab navigation cannot reach the backup destination.");
                    model.Section = "Destinations";
                    model.VerifyCommand.Execute(null);
                    Assert(model.IsBusy, "Repository operation did not enter busy state.");
                    window.UpdateLayout();
                    Assert(Descendants<ComboBox>((FrameworkElement)window.Content).Where(c => c.ItemsSource == model.Destinations).All(c => !c.IsEnabled) && Descendants<ListBox>((FrameworkElement)window.Content).Where(l => l.ItemsSource == model.Destinations).All(l => !l.IsEnabled), "A destination-changing control stayed enabled during verification.");
                    await FinishAsync(model.VerifyCommand);
                    Assert(model.LatestVerificationText.Contains("Failed") && dialogs.InformationMessages.Count > 0, "Verification failure vanished after closing the dialog.");
                }
                finally { window.Close(); }
            }
            Assert(dialogs.Errors.Count == 0, "Desktop commands reported errors.");
        });
    }
    private static async Task FinishAsync(ICommand command)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!command.CanExecute(null) && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);
        Assert(command.CanExecute(null), "Schedule command did not finish.");
    }
    private static bool Inside(FrameworkElement element, FrameworkElement parent)
    {
        var bounds = element.TransformToAncestor(parent).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= parent.ActualWidth + 1 && bounds.Bottom <= parent.ActualHeight + 1;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T value) yield return value;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private sealed class FixtureDialogs : DialogService
    {
        public List<Exception> Errors { get; } = [];
        public List<string> InformationMessages { get; } = [];
        public override void Error(Exception error) => Errors.Add(error);
        public override void Information(string message) => InformationMessages.Add(message);
    }
}
