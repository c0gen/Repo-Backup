using System.Collections.ObjectModel;
using System.Windows.Input;
using RepoBackup.Core.Models;
using RepoBackup.Desktop.Infrastructure;

namespace RepoBackup.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    public LoadState CatalogLoad { get; } = new();
    public LoadState ScheduleLoad { get; } = new();
    public LoadState DiscoveryLoad { get; } = new();
    public LoadState PreviewLoad { get; } = new();
    public LoadState SnapshotsLoad { get; } = new();
    public LoadState SnapshotFilesLoad { get; } = new();
    private readonly LoadState destinationLoad = new();
    public Func<CancellationToken, Task<DesktopCatalogSnapshot>>? CatalogLoader { get; set; }
    public Func<CancellationToken, Task<DesktopScheduleSnapshot>>? ScheduleLoader { get; set; }
    public Func<ProjectEntry, SavedSelection?, CancellationToken, Task<Preview>>? PreviewLoader { get; set; }
    public ObservableCollection<Destination> ScheduleDestinations { get; } = [];
    public ICommand RetryCatalogCommand { get; private set; } = null!;
    public ICommand RetrySchedulesCommand { get; private set; } = null!;
    public ICommand RetryPreviewCommand { get; private set; } = null!;
    public ICommand RetrySnapshotFilesCommand { get; private set; } = null!;
    public bool CanEditCatalog => IsIdle && CatalogLoad.IsReady;
    public bool CanEditSchedule => CanEditCatalog && ScheduleLoad.IsReady;
    public bool HasDestinations => Destinations.Count > 0;
    public bool HasConfiguredDestination => SelectedDestination?.RepositoryId is not null;
    public bool NeedsDestination => CatalogLoad.IsReady && !HasDestinations;
    public bool ShowProjectsEmpty => CatalogLoad.IsReady && !DiscoveryLoad.IsLoading && ProjectView.IsEmpty;
    public string ProjectsEmptyTitle => Projects.Count == 0 ? "Choose what to protect" : "No matching projects";
    public string ProjectsEmptyDetail => Projects.Count == 0 ? "Review discoveries or add a folder to get started." : "Clear the search or change the project filter.";
    public bool ShowDiscoveriesEmpty => CatalogLoad.IsReady && !DiscoveryLoad.IsLoading && !DiscoveryLoad.HasError && DiscoveryView.IsEmpty;
    public string DiscoveriesEmptyTitle => Discoveries.Count == 0 ? "No projects waiting for review" : "No matching discoveries";
    public string DiscoveriesEmptyDetail => Discoveries.Count == 0 ? "Refresh discovery, add a folder, or scan for repositories." : "Try another project name, path, or program.";
    public bool ShowSchedulesEmpty => ScheduleLoad.IsReady && Schedules.Count == 0;
    public bool ShowSnapshotsEmpty => SnapshotsLoad.IsReady && Snapshots.Count == 0;
    public bool ShowFilesEmpty => SnapshotFilesLoad.IsReady && FileView.IsEmpty;
    public string FilesEmptyText => SnapshotFiles.Count == 0 ? "No files were found in this recovery point." : "No files match your search.";
    public bool NeedsSnapshotSelection => !SnapshotsLoad.IsLoading && SelectedSnapshot is null && Snapshots.Count > 0;
    public bool NeedsPreviewSelection => CatalogLoad.IsReady && SelectedProject is null;
    private List<VerificationRecord> verifications = [];
    private bool settingsLoaded, applyingCatalog;
    private string destinationState = "Choose a destination", destinationFreeSpace = "";

    private void InitializeLoading()
    {
        foreach (var load in new[] { CatalogLoad, ScheduleLoad, DiscoveryLoad, PreviewLoad, SnapshotsLoad, SnapshotFilesLoad })
            load.PropertyChanged += (_, _) => RaiseLoadProperties();
        RetryCatalogCommand = new AsyncCommand(() => InitializeAsync(), dialogs.Error, () => !CatalogLoad.IsLoading && IsIdle);
        RetrySchedulesCommand = new AsyncCommand(RefreshSchedulesAsync, dialogs.Error, () => !ScheduleLoad.IsLoading && IsIdle);
        RetryPreviewCommand = new AsyncCommand(UpdatePreviewAsync, dialogs.Error, () => SelectedProject is not null);
        RetrySnapshotFilesCommand = new AsyncCommand(LoadSnapshotFilesAsync, dialogs.Error, () => SelectedSnapshot is not null);
        ScheduleEditor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ScheduleEditor.DestinationId)) Raise(nameof(SelectedScheduleDestination));
            if (e.PropertyName == nameof(ScheduleEditor.SelectionId)) Raise(nameof(SelectedScheduleScope));
            CommandManager.InvalidateRequerySuggested();
        };
    }
    private void RaiseLoadProperties()
    {
        foreach (var property in new[] { nameof(CanEditCatalog), nameof(CanEditSchedule), nameof(HasDestinations), nameof(HasConfiguredDestination),
            nameof(NeedsDestination), nameof(ShowProjectsEmpty), nameof(ProjectsEmptyTitle), nameof(ProjectsEmptyDetail),
            nameof(ShowDiscoveriesEmpty), nameof(DiscoveriesEmptyTitle), nameof(DiscoveriesEmptyDetail), nameof(ShowSchedulesEmpty),
            nameof(ShowSnapshotsEmpty), nameof(ShowFilesEmpty), nameof(FilesEmptyText), nameof(NeedsSnapshotSelection), nameof(NeedsPreviewSelection), nameof(ScheduleSummary) }) Raise(property);
        Setup?.Refresh();
        CommandManager.InvalidateRequerySuggested();
    }
    public async Task ReloadAsync() => await Task.WhenAll(ReloadCatalogAsync(), RefreshSchedulesAsync());
    private async Task ReloadCatalogAsync()
    {
        using var request = CatalogLoad.Begin();
        try
        {
            var data = await (CatalogLoader?.Invoke(request.Token) ?? DesktopCatalogReader.ReadAsync(Services, request.Token));
            if (!CatalogLoad.Accepts(request)) return;
            ApplyCatalog(data);
            CatalogLoad.Complete(request);
        }
        catch (OperationCanceledException) { CatalogLoad.Cancel(request); }
        catch (Exception error) { CatalogLoad.Fail(request, error); }
    }
    private void ApplyCatalog(DesktopCatalogSnapshot data)
    {
        // Capture at application time: edits made while a read was in flight are authoritative.
        var checkedIds = Projects.Where(p => p.IsSelected).Select(p => p.Id).ToHashSet();
        var currentId = SelectedProject?.Id;
        var destinationId = SelectedDestination?.Id ?? data.SelectedDestinationId;
        var selectionId = ActiveSelection?.Selection?.Id;
        applyingCatalog = true;
        try
        {
            foreach (var item in Projects.Concat(Discoveries).Concat(DismissedProjects)) item.PropertyChanged -= ProjectChanged;
            Projects.Clear(); Discoveries.Clear(); DismissedProjects.Clear();
            Activity.Clear(); foreach (var job in data.History) Activity.Add(job);
            verifications = data.Verifications;
            foreach (var project in data.Projects)
            {
                var item = new ProjectItem(project, data.History, Services.Catalog.SaveProject, data.AvailableProjects.Contains(project.Id)) { IsSelected = checkedIds.Contains(project.Id) };
                item.PropertyChanged += ProjectChanged;
                (project.Dismissed ? DismissedProjects : project.Reviewed ? Projects : Discoveries).Add(item);
            }
            Destinations.Clear(); foreach (var destination in data.Destinations) Destinations.Add(destination);
            SelectedDestination = Destinations.FirstOrDefault(d => d.Id == destinationId) ?? Destinations.FirstOrDefault();
            Selections.Clear(); Selections.Add(new("Full projects", null));
            foreach (var selection in data.Selections) Selections.Add(new(selection.Name, selection));
            activeSelection = Selections.FirstOrDefault(s => s.Selection?.Id == selectionId) ?? Selections[0]; Raise(nameof(ActiveSelection));
            SelectedProject = Projects.FirstOrDefault(p => p.Id == currentId) ?? Projects.FirstOrDefault();
            if (!settingsLoaded) { useVss = data.UseVss; Raise(nameof(UseVss)); settingsLoaded = true; }
            codexHookStatus = data.CodexHook; claudeHookStatus = data.ClaudeHook; antigravityHookStatus = data.AntigravityHook; RaiseHookStatus();
            ProjectSummary = $"{data.Projects.Count(p => !p.Dismissed)} projects discovered · {data.Projects.Where(p => !p.Dismissed).Sum(p => p.Roots.Count)} source roots";
            RefreshProtection();
        }
        finally { applyingCatalog = false; }
        _ = UpdatePreviewAsync(); _ = UpdateDestinationStatusAsync();
        foreach (var property in new[] { nameof(ProjectSummary), nameof(DiscoveryCount), nameof(HasDiscoveries), nameof(DiscoverySummary), nameof(SelectedCount), nameof(SelectedSummary), nameof(SelectedBackupLabel), nameof(EnabledSummary), nameof(LastRun), nameof(HasLastRun), nameof(LastRunTime), nameof(CoverageText), nameof(LatestVerificationText) }) Raise(property);
        RaiseLoadProperties();
    }
    public async Task RefreshSchedulesAsync()
    {
        using var request = ScheduleLoad.Begin();
        try
        {
            var data = await (ScheduleLoader?.Invoke(request.Token) ?? DesktopCatalogReader.ReadSchedulesAsync(Services, request.Token));
            if (!ScheduleLoad.Accepts(request)) return;
            var destinationId = ScheduleEditor.DestinationId;
            var selectionId = ScheduleEditor.SelectionId;
            // WPF selection bindings may emit null when their item sources are cleared.
            ScheduleEditor.PreserveDraft(() =>
            {
                ScheduleDestinations.Clear(); foreach (var destination in data.Destinations) ScheduleDestinations.Add(destination);
                ScheduleScopes.Clear(); ScheduleScopes.Add(new("All enabled projects", null));
                foreach (var selection in data.Selections) ScheduleScopes.Add(new(selection.Name, selection));
                Schedules.Clear(); foreach (var schedule in data.Schedules) Schedules.Add(schedule);
                selectedSchedule = Schedules.FirstOrDefault(s => s.Id == ScheduleEditor.Id); Raise(nameof(SelectedSchedule));
            });
            ScheduleEditor.DestinationId = destinationId ?? SelectedDestination?.Id ?? data.Destinations.FirstOrDefault()?.Id;
            ScheduleEditor.SelectionId = selectionId;
            Raise(nameof(SelectedScheduleDestination)); Raise(nameof(SelectedScheduleScope));
            ScheduleLoad.Complete(request);
        }
        catch (OperationCanceledException) { ScheduleLoad.Cancel(request); }
        catch (Exception error) { ScheduleLoad.Fail(request, error); }
    }
    private async Task UpdateDestinationStatusAsync()
    {
        using var request = destinationLoad.Begin(DestinationIdentity);
        var destination = SelectedDestination;
        destinationState = destination is null ? "Choose a destination" : "Checking destination…";
        destinationFreeSpace = ""; Raise(nameof(DestinationState)); Raise(nameof(DestinationFreeSpace));
        try
        {
            var result = await Task.Run(() => destination is null ? ("Choose a destination", "") :
                (!Directory.Exists(destination.Path) ? "Drive unavailable" : destination.RepositoryId is null ? "Repository not initialized" : "Repository configured",
                freeSpace(destination.Path) is { } space ? FormatBytes(space) + " free" : ""), request.Token);
            if (!destinationLoad.Accepts(request)) return;
            (destinationState, destinationFreeSpace) = result; destinationLoad.Complete(request);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error) { if (!destinationLoad.Accepts(request)) return; destinationState = "Destination check failed: " + error.Message; }
        Raise(nameof(DestinationState)); Raise(nameof(DestinationFreeSpace));
    }
    public void StopBackgroundReads()
    {
        foreach (var load in new[] { CatalogLoad, ScheduleLoad, DiscoveryLoad, PreviewLoad, SnapshotsLoad, SnapshotFilesLoad, destinationLoad })
            if (load.IsLoading) load.Cancel();
    }
}
