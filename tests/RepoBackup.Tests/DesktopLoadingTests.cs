using System.Windows.Controls;
using System.Windows.Threading;
using RepoBackup.Core.Discovery;
using RepoBackup.Core.Models;
using RepoBackup.Desktop.ViewModels;
using static RepoBackup.Tests.TestRunner;
using static RepoBackup.Tests.DesktopTestSupport;

namespace RepoBackup.Tests;

public static class DesktopLoadingTests
{
    private static TaskCompletionSource<T> Pending<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static async Task RunAsync(TestRunner tests, string root)
    {
        await tests.Run("Initial catalog loading renders before data arrives and failed reads remain retryable", async () =>
        {
            var model = Model(Path.Combine(root, "catalog"), out _);
            var pending = Pending<DesktopCatalogSnapshot>(); model.CatalogLoader = _ => pending.Task;
            var load = model.InitializeAsync(discover: false, showSetup: false);
            Assert(model.CatalogLoad.IsLoading && !model.ShowProjectsEmpty && !model.NeedsDestination && !model.AddFolderCommand.CanExecute(null), "Initial loading was displayed as empty or editable.");
            var dispatched = false; await Dispatcher.CurrentDispatcher.InvokeAsync(() => dispatched = true);
            Assert(dispatched && !load.IsCompleted, "Catalog loading blocked the dispatcher.");
            pending.SetException(new IOException("Fixture catalog temporarily unavailable")); await load;
            Assert(model.CatalogLoad.HasError && !model.CatalogLoad.IsLoading && !model.ShowProjectsEmpty, "Failed catalog read became an empty list.");
            model.CatalogLoader = null; await ExecuteAsync(model.RetryCatalogCommand);
            Assert(model.CatalogLoad.IsReady && model.NeedsDestination, "Catalog retry did not recover.");
            model.Setup.Dismiss();
            await WithWindowAsync(model, window => { Render(window, Path.Combine(root, "projects-empty.png")); return Task.CompletedTask; });
        });
        await tests.Run("Scheduling loading preserves the bound draft, destination, scope and identity", async () =>
        {
            var fixture = Path.Combine(root, "schedules"); var model = Model(fixture, out _);
            var first = Destination(model, fixture, "First"); var second = Destination(model, fixture, "Second");
            var project = Project(model, fixture, "Alpha", approved: true);
            var selection = new SavedSelection { Name = "Alpha subset", ProjectId = project.Id, Paths = [project.Roots[0].Path] }; model.Services.Catalog.SaveSelection(selection);
            await model.ReloadAsync(); model.SelectedDestination = first; model.ScheduleEditor.New(second.Id); model.ScheduleEditor.SelectionId = selection.Id;
            var schedule = model.ScheduleEditor.Build(); model.ScheduleEditor.Saved(schedule); model.Services.Catalog.SaveSchedules([schedule]);
            model.ScheduleEditor.Name = "Unsaved name"; model.ScheduleEditor.Time = "21:45";
            model.Section = "Settings"; await model.RefreshSchedulesAsync();
            await WithWindowAsync(model, async window =>
            {
                var data = await DesktopCatalogReader.ReadSchedulesAsync(model.Services, default);
                var old = Pending<DesktopScheduleSnapshot>(); var newest = Pending<DesktopScheduleSnapshot>(); var count = 0; CancellationToken oldToken = default;
                model.ScheduleLoader = token => { if (++count == 1) { oldToken = token; return old.Task; } return newest.Task; };
                var oldLoad = model.RefreshSchedulesAsync(); var latest = model.RefreshSchedulesAsync();
                Assert(model.ScheduleLoad.IsLoading && !model.SaveScheduleCommand.CanExecute(null) && model.IsIdle, "Schedule read did not isolate its loading state.");
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                Assert(Descendants<ProgressBar>(window).Any(p => p.IsVisible && p.IsIndeterminate), "Schedule loading indicator was not rendered.");
                Render(window, Path.Combine(root, "schedules-loading.png"));
                old.SetException(new IOException("Stale error")); await oldLoad;
                Assert(oldToken.IsCancellationRequested && model.ScheduleLoad.IsLoading && !model.ScheduleLoad.HasError, "Stale schedule error cleared the current load.");
                newest.SetResult(data); await latest; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert(model.ScheduleEditor.Name == "Unsaved name" && model.ScheduleEditor.Time == "21:45" && model.ScheduleEditor.Id == schedule.Id && model.ScheduleEditor.DestinationId == second.Id && model.ScheduleEditor.SelectionId == selection.Id && model.SelectedDestination?.Id == first.Id, "Schedule reload overwrote its draft or global destination.");
                Assert(model.SelectedSchedule?.Id == schedule.Id && model.SaveScheduleCommand.CanExecute(null), "Schedule identity or editability was lost.");
                var destinationChoice = Descendants<ComboBox>(window).Single(c => ReferenceEquals(c.ItemsSource, model.ScheduleDestinations));
                var scopeChoice = Descendants<ComboBox>(window).Single(c => ReferenceEquals(c.ItemsSource, model.ScheduleScopes));
                Assert(destinationChoice.SelectedItem is Destination visibleDestination && visibleDestination.Id == second.Id && scopeChoice.SelectedItem is SelectionOption visibleScope && visibleScope.Selection?.Id == selection.Id, "Schedule choices were retained in the model but disappeared from the dropdowns.");
                model.ScheduleLoader = _ => throw new IOException("Fixture schedule read failed"); await model.RefreshSchedulesAsync();
                Assert(model.ScheduleLoad.HasError && !model.ShowSchedulesEmpty, "A failed schedule read became an empty list.");
                Render(window, Path.Combine(root, "schedules-error.png"));
                model.ScheduleLoader = null; await ExecuteAsync(model.RetrySchedulesCommand);
                Assert(model.ScheduleLoad.IsReady && model.ScheduleEditor.Name == "Unsaved name", "Retry discarded the draft.");
                model.ScheduleEditor.SelectionId = null; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert(scopeChoice.SelectedItem is SelectionOption { Selection: null }, "The all-enabled scope was shown as an empty dropdown.");
            });
        });
        await tests.Run("Preview loading rejects stale results and errors without clearing a newer indicator", async () =>
        {
            var fixture = Path.Combine(root, "preview"); var model = Model(fixture, out _);
            Project(model, fixture, "Alpha", approved: true); Project(model, fixture, "Beta", approved: true); await model.ReloadAsync();
            var old = Pending<Preview>(); var newest = Pending<Preview>(); CancellationToken oldToken = default;
            model.PreviewLoader = (project, _, token) => { if (project.Name == "Alpha") { oldToken = token; return old.Task; } return newest.Task; };
            var oldLoad = model.RefreshPreviewAsync(); model.SelectedProject = model.Projects.Single(p => p.Name == "Beta"); var latest = model.RefreshPreviewAsync();
            await WithWindowAsync(model, async window =>
            {
                Assert(model.PreviewLoad.IsLoading && model.PreviewFiles == "—" && model.SourceTree.Count == 0 && model.IsIdle, "Preview showed stale contents or blocked unrelated work.");
                Render(window, Path.Combine(root, "preview-loading.png"));
                old.SetResult(Preview("Old project warning")); await oldLoad;
                Assert(oldToken.IsCancellationRequested && model.PreviewLoad.IsLoading && !model.PreviewWarnings.Contains("Old"), "Stale preview replaced the newer request.");
                newest.SetResult(Preview("Current project warning")); await latest;
                Assert(model.PreviewLoad.IsReady && model.PreviewWarnings == "Current project warning", "Latest preview did not settle.");
                model.PreviewLoader = (_, _, _) => throw new IOException("Fixture source unavailable"); await model.RefreshPreviewAsync();
                Assert(model.PreviewLoad.HasError && !model.PreviewLoad.IsLoading, "Preview error was hidden.");
                Render(window, Path.Combine(root, "preview-error.png"));
                model.PreviewLoader = (_, _, _) => Task.FromResult(Preview()); await ExecuteAsync(model.RetryPreviewCommand);
                Assert(model.PreviewLoad.IsReady && !model.PreviewLoad.HasError, "Preview retry failed.");
            });
        });
        await tests.Run("Discovery loading separates empty, warning, search and failed results", async () =>
        {
            var fixture = Path.Combine(root, "discovery"); var model = Model(fixture, out _); await model.ReloadAsync();
            var pending = Pending<DiscoveryResult>(); model.DiscoveryLoader = _ => pending.Task;
            var load = model.RefreshDiscoveryAsync();
            Assert(model.DiscoveryLoad.IsLoading && !model.ShowDiscoveriesEmpty && model.IsIdle, "Discovery looked empty while loading.");
            pending.SetResult(new(0, 0, 0, ["Fixture provider warning"])); await load;
            Assert(model.ShowDiscoveriesEmpty && model.HasDiscoveryWarnings, "Empty discovery lost provider warnings or next-step guidance.");
            var candidate = Project(model, fixture, "Candidate"); await model.ReloadAsync(); model.DiscoverySearch = "not-a-match";
            Assert(model.ShowDiscoveriesEmpty && model.DiscoveriesEmptyTitle.Contains("matching"), "Filtered discovery was mistaken for an empty inbox.");
            model.DiscoverySearch = "";
            model.DiscoveryLoader = _ => throw new IOException("Fixture discovery failure"); await model.RefreshDiscoveryAsync();
            Assert(model.DiscoveryLoad.HasError && !model.ShowDiscoveriesEmpty, "Discovery failure was shown as empty.");
            model.DiscoveryLoader = _ => Task.FromResult(new DiscoveryResult(0, 1, 1, [])); await ExecuteAsync(model.RefreshCommand);
            Assert(model.DiscoveryLoad.IsReady && model.Discoveries.Single().Id == candidate.Id, "Discovery retry lost the candidate.");
            var stale = Pending<DiscoveryResult>(); model.DiscoveryLoader = _ => stale.Task; var staleLoad = model.RefreshDiscoveryAsync(); model.CancelDiscovery();
            model.DiscoveryLoader = _ => Task.FromResult(new DiscoveryResult(0, 1, 1, [])); await model.RefreshDiscoveryAsync();
            stale.SetException(new IOException("Cancelled discovery")); await staleLoad;
            Assert(model.DiscoveryLoad.IsReady && !model.DiscoveryLoad.HasError, "Cancelled discovery overwrote the newer result.");
        });
        await tests.Run("Snapshot and file loading follows the latest destination and selection", async () =>
        {
            var fixture = Path.Combine(root, "snapshots"); var model = Model(fixture, out _);
            var first = Destination(model, fixture, "First"); var second = Destination(model, fixture, "Second"); await model.ReloadAsync();
            var pending = Pending<List<SnapshotInfo>>(); model.SnapshotLoader = (destination, _) => destination.Id == first.Id ? pending.Task : Task.FromResult(new List<SnapshotInfo>());
            model.SelectedDestination = first; var oldLoad = model.RefreshSnapshotsAsync();
            Assert(model.SnapshotsLoad.IsLoading && !model.ShowSnapshotsEmpty, "Snapshot browser looked empty while loading.");
            model.SelectedDestination = second; await model.RefreshSnapshotsAsync();
            pending.SetException(new IOException("Old drive unavailable")); await oldLoad;
            Assert(model.ShowSnapshotsEmpty && !model.SnapshotsLoad.HasError, "Old destination overwrote the new empty result.");
            var snapshot = new SnapshotItem(new(new string('b', 64), DateTimeOffset.UtcNow, [], []));
            var oldFiles = Pending<List<SnapshotFileItem>>(); var newFiles = Pending<List<SnapshotFileItem>>();
            model.SnapshotFileLoader = (_, item, _) => item.Id == snapshot.Snapshot.Id ? oldFiles.Task : newFiles.Task;
            model.SelectedSnapshot = snapshot; var oldFileLoad = model.RefreshSnapshotFilesAsync();
            model.SelectedSnapshot = new(new(new string('c', 64), DateTimeOffset.UtcNow, [], [])); var newFileLoad = model.RefreshSnapshotFilesAsync();
            oldFiles.SetResult([new(new("/stale.txt", "file", 1), "stale.txt")]); await oldFileLoad;
            Assert(model.SnapshotFilesLoad.IsLoading && model.SnapshotFiles.Count == 0, "Old files cleared the current indicator.");
            newFiles.SetResult([new(new("/current.txt", "file", 1), "current.txt")]); await newFileLoad;
            model.FileSearch = "missing"; Assert(model.ShowFilesEmpty && model.FilesEmptyText.Contains("search"), "File search did not explain no results.");
            model.SnapshotFileLoader = (_, _, _) => throw new IOException("Fixture file read failed"); await model.RefreshSnapshotFilesAsync();
            Assert(model.SnapshotFilesLoad.HasError && !model.ShowFilesEmpty, "File read failure looked empty.");
            model.SnapshotFileLoader = (_, _, _) => Task.FromResult(new List<SnapshotFileItem>()); await ExecuteAsync(model.RetrySnapshotFilesCommand);
            Assert(model.SnapshotFilesLoad.IsReady && model.ShowFilesEmpty, "File retry did not recover.");
            model.SnapshotLoader = (_, _) => Task.FromResult(new List<SnapshotInfo>()); model.Section = "Snapshots & Restore"; await model.RefreshSnapshotsAsync();
            await WithWindowAsync(model, window => { Render(window, Path.Combine(root, "snapshots-empty.png")); return Task.CompletedTask; });
        });
    }
}
