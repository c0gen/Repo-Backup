using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using RepoBackup.Core.Application;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using RepoBackup.Desktop.Infrastructure;

namespace RepoBackup.Desktop.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public ApplicationServices Services { get; }
    private readonly DialogService dialogs;
    private readonly Func<string, long?> freeSpace;
    public SetupWizardViewModel Setup { get; }
    public ObservableCollection<ProjectItem> Projects { get; } = [];
    public ObservableCollection<ProjectItem> DismissedProjects { get; } = [];
    public ObservableCollection<ProjectItem> Discoveries { get; } = [];
    public ObservableCollection<Destination> Destinations { get; } = [];
    public ObservableCollection<SelectionOption> Selections { get; } = [];
    public ObservableCollection<SelectionOption> ScheduleScopes { get; } = [];
    public ObservableCollection<SourceNode> SourceTree { get; } = [];
    public ObservableCollection<SnapshotItem> Snapshots { get; } = [];
    public ObservableCollection<SnapshotFileItem> SnapshotFiles { get; } = [];
    public ObservableCollection<JobRecord> Activity { get; } = [];
    public ObservableCollection<ScheduleDefinition> Schedules { get; } = [];
    public ICollectionView ProjectView { get; }
    public ICollectionView DiscoveryView { get; }
    public ICollectionView FileView { get; }
    public string[] ProjectFilters { get; } = ["All projects", "Enabled", "Needs backup", "Unavailable"];
    public ScheduleKind[] ScheduleKinds { get; } = Enum.GetValues<ScheduleKind>();
    public DayOfWeek[] ScheduleDays { get; } = Enum.GetValues<DayOfWeek>();
    private string section = "Projects";
    public string Section { get => section; set { if (Set(ref section, value)) { Raise(nameof(IsProjects)); Raise(nameof(IsDiscoveries)); Raise(nameof(IsSnapshots)); Raise(nameof(IsDestinations)); Raise(nameof(IsActivity)); Raise(nameof(IsSettings)); if (IsSnapshots && SelectedDestination is not null) _ = RefreshSnapshotsAsync(); if (IsSettings) _ = RefreshSchedulesAsync(); } } }
    public bool IsProjects => Section == "Projects";
    public bool IsDiscoveries => Section == "Discoveries";
    public bool IsSnapshots => Section == "Snapshots & Restore";
    public bool IsDestinations => Section == "Destinations";
    public bool IsActivity => Section == "Activity";
    public bool IsSettings => Section == "Settings";
    private bool busy;
    public bool IsBusy { get => busy; private set { if (Set(ref busy, value)) { Raise(nameof(IsIdle)); RaiseLoadProperties(); CommandManager.InvalidateRequerySuggested(); } } }
    public bool IsIdle => !IsBusy;
    private string status = "Ready";
    public string StatusMessage { get => status; private set => Set(ref status, value); }
    private string search = "";
    public string Search { get => search; set { if (Set(ref search, value)) RefreshProjectView(); } }
    private string discoverySearch = "";
    public string DiscoverySearch { get => discoverySearch; set { if (Set(ref discoverySearch, value)) { DiscoveryView.Refresh(); RaiseLoadProperties(); } } }
    private string filter = "All projects";
    public string ProjectFilter { get => filter; set { if (Set(ref filter, value)) RefreshProjectView(); } }
    private string fileSearch = "";
    public string FileSearch { get => fileSearch; set { if (Set(ref fileSearch, value)) { FileView.Refresh(); RaiseLoadProperties(); } } }
    private ProjectItem? selectedProject;
    public ProjectItem? SelectedProject { get => selectedProject; set { if (Set(ref selectedProject, value)) { if (ActiveSelection?.Selection is { } s && s.ProjectId != value?.Id) ActiveSelection = Selections.FirstOrDefault(); if (!applyingCatalog) _ = UpdatePreviewAsync(); Raise(nameof(PreviewTitle)); } } }
    private Destination? selectedDestination;
    private string? browserDestinationId;
    public Destination? SelectedDestination { get => selectedDestination; set { if (Set(ref selectedDestination, value)) { Raise(nameof(DestinationState)); Raise(nameof(DestinationFreeSpace)); catalogDestinationChanged(); } } }
    private void catalogDestinationChanged()
    {
        if (!applyingCatalog && selectedDestination is not null) Services.Catalog.SaveSetting("selectedDestination", selectedDestination.Id);
        RefreshProtection();
        Raise(nameof(LatestVerificationText));
        if (!applyingCatalog) _ = UpdateDestinationStatusAsync();
        RaiseLoadProperties();
        var identity = DestinationIdentity;
        if (browserDestinationId == identity) return;
        browserDestinationId = identity;
        SnapshotsLoad.Cancel(); SnapshotFilesLoad.Cancel();
        Snapshots.Clear(); SelectedSnapshot = null; SnapshotFiles.Clear(); SelectedSnapshotFile = null;
        if (IsSnapshots && IsIdle) _ = RefreshSnapshotsAsync();
    }
    private string DestinationIdentity => SelectedDestination is { } d ? d.Id + "|" + d.Path + "|" + d.RepositoryId : "";
    private void RefreshProtection()
    {
        var history = Activity;
        foreach (var project in Projects) project.UpdateProtection(history, SelectedDestination?.Id);
        RefreshProjectView();
    }
    private void RefreshProjectView() { ProjectView.Refresh(); Raise(nameof(SelectedSummary)); RaiseLoadProperties(); }
    public string LatestVerificationText
    {
        get
        {
            var record = verifications.Where(v => v.DestinationId == SelectedDestination?.Id).OrderByDescending(v => v.At).FirstOrDefault();
            return record is null ? "No repository data verification recorded for this destination." : $"Latest repository verification: {record.Outcome} · {record.At.LocalDateTime:g}\n{record.Message}";
        }
    }

    public string DestinationState => destinationState;
    public string DestinationFreeSpace => destinationFreeSpace;
    private SelectionOption? activeSelection;
    public SelectionOption? ActiveSelection { get => activeSelection; set { if (Set(ref activeSelection, value)) { if (value?.Selection is { } s && SelectedProject?.Id != s.ProjectId) SelectedProject = Projects.FirstOrDefault(p => p.Id == s.ProjectId); if (!applyingCatalog) _ = UpdatePreviewAsync(); Raise(nameof(CoverageText)); Raise(nameof(SelectedBackupLabel)); } } }
    public string CoverageText => ActiveSelection?.Selection is null ? "Full project" : "Partial selection";
    public string PreviewTitle => ActiveSelection?.Selection?.Name ?? SelectedProject?.Name ?? "Select a project";
    private Preview? preview;
    public string PreviewFiles => preview is null ? "—" : preview.FileCount.ToString("N0");
    public string PreviewSize => preview is null ? "—" : FormatBytes(preview.Bytes);
    public string PreviewWarnings => preview is null ? "" : string.Join("\n", preview.Warnings);
    public bool HasPreviewWarnings => !string.IsNullOrEmpty(PreviewWarnings);
    public string ExclusionDetails => string.Join("  ·  ", (ActiveSelection?.Selection?.Exclusions ?? SelectedProject?.Project.Exclusions ?? new()).ExcludedDirectories);
    public string ExclusionText
    {
        get
        {
            var rules = ActiveSelection?.Selection?.Exclusions ?? SelectedProject?.Project.Exclusions ?? new();
            var featured = rules.ExcludedDirectories.Where(d => d is "node_modules" or ".venv" or "__pycache__").ToList();
            var others = rules.ExcludedDirectories.Count - featured.Count;
            return string.Join("  ·  ", featured) + (others > 0 ? $"\n+{others} other cache directories" : "") + (rules.RelativePatterns.Count > 0 ? $"\n{rules.RelativePatterns.Count} custom patterns" : "");
        }
    }
    public int SelectedCount => Projects.Count(p => p.IsSelected);
    public string SelectedSummary => $"{SelectedCount} selected · {Projects.Count(p => p.IsSelected && !ProjectView.Cast<ProjectItem>().Contains(p))} hidden by filters";
    public string SelectedBackupLabel => ActiveSelection?.Selection is { } selection ? "Back Up “" + selection.Name + "”" : $"Back Up Selected ({SelectedCount})";
    public string EnabledSummary => $"{Projects.Count(p => p.Enabled)} enabled";
    public int DiscoveryCount => Discoveries.Count;
    public bool HasDiscoveries => DiscoveryCount > 0;
    public string DiscoverySummary => $"{DiscoveryCount} new project{(DiscoveryCount == 1 ? "" : "s")} need review";
    public string ProjectSummary { get; private set; } = "Discovering projects…";
    public string ScheduleSummary => ScheduleLoad.IsLoading ? "Loading schedules…" : ScheduleLoad.HasError ? "Schedules unavailable" : !ScheduleLoad.HasLoaded ? "Loading schedules…" : Schedules.Any(s => s.Enabled) ? $"{Schedules.Count(s => s.Enabled)} schedule(s) enabled" : "Scheduling off";
    public JobRecord? LastRun => Activity.FirstOrDefault(j => j.FinishedAt is not null);
    public bool HasLastRun => LastRun is not null;
    public string LastRunTime => LastRun?.FinishedAt?.LocalDateTime.ToString("MMM d, h:mm tt") ?? "No completed runs yet";
    private JobRecord? selectedActivity;
    public JobRecord? SelectedActivity { get => selectedActivity; set { if (Set(ref selectedActivity, value)) Raise(nameof(ActivityDetail)); } }
    public string ActivityDetail => SelectedActivity is null ? "Select a run to inspect its result." : string.Join("\n", SelectedActivity.Messages.DefaultIfEmpty("No coverage warnings were reported."));
    private SnapshotItem? selectedSnapshot;
    public SnapshotItem? SelectedSnapshot { get => selectedSnapshot; set { if (Set(ref selectedSnapshot, value)) _ = LoadSnapshotFilesAsync(); } }
    private SnapshotFileItem? selectedSnapshotFile;
    public SnapshotFileItem? SelectedSnapshotFile { get => selectedSnapshotFile; set { if (Set(ref selectedSnapshotFile, value)) CommandManager.InvalidateRequerySuggested(); } }
    public ICommand NavigateCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ToggleAllCommand { get; }
    public ICommand RefreshCommand { get; private set; } = null!;
    public ICommand AddFolderCommand { get; private set; } = null!;
    public ICommand ScanCommand { get; private set; } = null!;
    public ICommand ApproveCommand { get; private set; } = null!;
    public ICommand DismissCommand { get; private set; } = null!;
    public ICommand RelinkCommand { get; private set; } = null!;
    public ICommand SaveSelectionCommand { get; private set; } = null!;
    public ICommand EditExclusionsCommand { get; private set; } = null!;
    public ICommand BackupSelectedCommand { get; private set; } = null!;
    public ICommand BackupAllCommand { get; private set; } = null!;
    public ICommand AddDestinationCommand { get; private set; } = null!;
    public ICommand VerifyCommand { get; private set; } = null!;
    public ICommand ExportKeyCommand { get; private set; } = null!;
    public ICommand RefreshSnapshotsCommand { get; private set; } = null!;
    public ICommand RestoreCommand { get; private set; } = null!;
    public ICommand RestoreFileCommand { get; private set; } = null!;
    public ICommand TestRestoreCommand { get; private set; } = null!;
    public ICommand RecoverCatalogCommand { get; private set; } = null!;
    public ICommand ExportCatalogCommand { get; private set; } = null!;
    public ICommand ImportCatalogCommand { get; private set; } = null!;
    public ICommand InstallHookCommand { get; private set; } = null!;
    public ICommand InstallClaudeHookCommand { get; private set; } = null!;
    public ICommand InstallAntigravityHookCommand { get; private set; } = null!;
    public ICommand SaveScheduleCommand { get; private set; } = null!;
    public ICommand DeleteScheduleCommand { get; private set; } = null!;

    public MainViewModel(ApplicationServices services, DialogService dialogs, Func<string, long?>? freeSpace = null)
    {
        Services = services; this.dialogs = dialogs; this.freeSpace = freeSpace ?? PathSafety.FreeSpace;
        ProjectView = CollectionViewSource.GetDefaultView(Projects); ProjectView.Filter = FilterProject;
        DiscoveryView = CollectionViewSource.GetDefaultView(Discoveries); DiscoveryView.Filter = item => ((ProjectItem)item).Matches(DiscoverySearch);
        FileView = CollectionViewSource.GetDefaultView(SnapshotFiles); FileView.Filter = item => item is SnapshotFileItem file && file.DisplayPath.Contains(FileSearch, StringComparison.OrdinalIgnoreCase);
        NavigateCommand = new RelayCommand(p => Section = (string)p!);
        CancelCommand = new RelayCommand(_ => Cancel(), _ => IsBusy);
        ToggleAllCommand = new RelayCommand(_ => { var visible = ProjectView.Cast<ProjectItem>().ToList(); var select = visible.Any(p => !p.IsSelected); foreach (var p in visible) p.IsSelected = select; });
        Setup = new SetupWizardViewModel(this);
        InitializeLoading(); InitializeProjectCommands(); InitializeBackupCommands(); InitializeSnapshotCommands(); InitializeSettingsCommands();
    }
    private bool FilterProject(object item)
    {
        var p = (ProjectItem)item;
        if (!p.Matches(Search)) return false;
        return ProjectFilter switch { "Enabled" => p.Enabled, "Needs backup" => p.NeedsBackup, "Unavailable" => !p.Available, _ => true };
    }
    private AsyncCommand Command(Func<Task> action, Func<bool>? available = null) => new(action, dialogs.Error, () => CanEditCatalog && (available?.Invoke() ?? true));
    public async Task InitializeAsync(bool discover = true, bool showSetup = true)
    {
        await ReloadCatalogAsync();
        await RefreshSchedulesAsync();
        if (!CatalogLoad.IsReady) return;
        try { await Setup.InitializeAsync(showSetup); }
        catch (Exception error) { CatalogLoad.SetError(error); return; }
        if (discover && !Setup.IsOpen && HasConfiguredDestination) await RefreshAsync();
    }
    private void ProjectChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectItem.IsSelected)) { Raise(nameof(SelectedCount)); Raise(nameof(SelectedSummary)); Raise(nameof(SelectedBackupLabel)); }
        if (e.PropertyName == nameof(ProjectItem.Enabled)) { Raise(nameof(EnabledSummary)); RefreshProjectView(); Setup.Refresh(); }
        CommandManager.InvalidateRequerySuggested();
    }
    public static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.##} GB" : bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.##} MB" : bytes >= 1024 ? $"{bytes / 1024d:0.##} KB" : $"{bytes} B";
}
