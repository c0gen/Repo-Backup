using System.Windows.Input;
using RepoBackup.Core.Models;
using RepoBackup.Desktop.Infrastructure;

namespace RepoBackup.Desktop.ViewModels;

public sealed partial class SnapshotBrowserViewModel : ObservableObject
{
    private readonly Func<bool> canRead;
    private readonly Dictionary<string, BrowserState> states = new(StringComparer.Ordinal);
    private BrowserState state = new();
    private Destination? destination;
    private string identity = "";
    private bool publishing;
    public LoadState SummariesLoad { get; } = new();
    public LoadState FilesLoad { get; } = new();
    public PageCollection<SnapshotItem> Snapshots { get; } = [];
    public PageCollection<SnapshotFileItem> Files { get; } = [];
    public PageCollection<RecoverySeriesOption> SeriesOptions { get; } = [];
    public Func<Destination, CancellationToken, Task<List<SnapshotSummary>>> SummaryLoader { get; set; } = null!;
    public Func<Destination, SnapshotSummary, CancellationToken, Task<List<SnapshotFileItem>>> FileLoader { get; set; } = null!;
    public Func<string, string> CanonicalSeries { get; set; } = s => s;
    public Func<string, string?> SeriesLabel { get; set; } = _ => null;
    public ICommand OpenFolderCommand { get; }
    public ICommand UpCommand { get; }
    public ICommand RootCommand { get; }
    public ICommand PreviousPageCommand { get; }
    public ICommand NextPageCommand { get; }
    public string Notice { get => notice; private set => Set(ref notice, value); }
    private string notice = "";
    public string CacheStatus => state.LoadedAt is { } time ? $"{(!canRead() || state.Stale ? "Cached · " : "Loaded · ")}{time.LocalDateTime:g}" : "";
    public double SnapshotScroll { get => state.SnapshotScroll; set => state.SnapshotScroll = value; }
    public double FileScroll { get => state.FileScroll; set => state.FileScroll = value; }
    private SnapshotItem? selected;
    public SnapshotItem? SelectedSnapshot
    {
        get => selected;
        set
        {
            if (publishing || !Set(ref selected, value)) return;
            state.SelectedId = value?.Snapshot.Id;
            state.FilePath = null; state.Folder = ""; state.Page = 0; state.FileScroll = 0;
            RaiseFileState();
            _ = LoadFilesAsync();
        }
    }
    private SnapshotFileItem? selectedFile;
    public SnapshotFileItem? SelectedFile
    {
        get => selectedFile;
        set { if (publishing) return; if (Set(ref selectedFile, value)) { state.FilePath = value?.Path; CommandManager.InvalidateRequerySuggested(); } }
    }

    public SnapshotBrowserViewModel(Func<bool> canRead)
    {
        this.canRead = canRead;
        OpenFolderCommand = new RelayCommand(_ => NavigateFolder(SelectedFile!.Path), _ => SelectedFile?.File.Type == "dir");
        UpCommand = new RelayCommand(_ => NavigateFolder(ParentFolder()), _ => state.Folder.Length > 0);
        RootCommand = new RelayCommand(_ => NavigateFolder(""));
        PreviousPageCommand = new RelayCommand(_ => ChangePage(-1), _ => state.Page > 0);
        NextPageCommand = new RelayCommand(_ => ChangePage(1), _ => (state.Page + 1) * PageSize < matches.Length);
    }

    public void SetDestination(Destination? value)
    {
        var key = value is null ? "" : value.Id + "|" + System.IO.Path.GetFullPath(value.Path).TrimEnd('\\').ToUpperInvariant() + "|" + value.RepositoryId;
        destination = value;
        if (identity == key) return;
        Cancel(); identity = key;
        if (!states.TryGetValue(key, out var saved)) states[key] = saved = new();
        state = saved; index = null; matches = []; selectedFile = null; Files.Replace([]);
        RestoreFilters(); ApplySummaries(); Raise(nameof(CacheStatus)); RaiseFileState();
    }

    public async Task ActivateAsync()
    {
        if (state.Stale || state.LoadedAt is null) await RefreshAsync();
        else if (SelectedSnapshot is not null && index is null) await LoadFilesAsync();
    }

    public async Task RefreshAsync()
    {
        var target = destination;
        if (target is null) return;
        if (!canRead()) { Notice = "Pause the backup queue to load fresh recovery points. Cached backups remain available."; Raise(nameof(CacheStatus)); return; }
        using var request = SummariesLoad.Begin(identity);
        try
        {
            var result = await SummaryLoader(target, request.Token);
            if (!SummariesLoad.Accepts(request)) return;
            state.All = result.OrderByDescending(x => x.Time).ThenBy(x => x.Id, StringComparer.Ordinal).Select(x => new SnapshotItem(x)).ToList();
            state.LoadedAt = DateTimeOffset.Now; state.Stale = false; Notice = "";
            if (!state.Visited) { state.Visited = true; state.SelectedId = state.All.FirstOrDefault()?.Snapshot.Id; }
            if (state.SelectedId is { } id && !state.All.Any(x => x.Snapshot.Id == id))
            {
                state.SelectedId = null; state.FilePath = null; Notice = "The selected recovery point is no longer available. Choose another backup.";
            }
            ApplySummaries(); SummariesLoad.Complete(request); Raise(nameof(CacheStatus));
            await LoadFilesAsync();
        }
        catch (OperationCanceledException) { SummariesLoad.Cancel(request); }
        catch (Exception error) { SummariesLoad.Fail(request, error); Notice = "Could not refresh recovery points. Previously loaded backups remain available."; }
    }

    private void ApplySummaries()
    {
        publishing = true;
        try
        {
            SeriesOptions.Replace(new[] { new RecoverySeriesOption("", "All projects / selections") }.Concat(state.All
                .GroupBy(x => CanonicalSeries(x.Snapshot.TagValue("series") ?? x.Snapshot.Id))
                .Select(g => new RecoverySeriesOption(g.Key, SeriesLabel(g.Key) ?? g.First().Name)).OrderBy(x => x.Label)));
            Snapshots.Replace(state.All.Where(MatchesSnapshot));
            selected = Snapshots.FirstOrDefault(x => x.Snapshot.Id == state.SelectedId);
            if (selected is null) { index = null; matches = []; selectedFile = null; Files.Replace([]); }
        }
        finally { publishing = false; }
        Raise(nameof(SelectedSnapshot)); Raise(nameof(SelectedFile)); Raise(nameof(SelectedSeries)); RaiseFileState();
        CommandManager.InvalidateRequerySuggested();
    }

    public void RepositoryStateChanged()
    {
        if (!canRead()) Cancel();
        Raise(nameof(CacheStatus));
    }
    public void Invalidate(IEnumerable<string> destinationIds)
    {
        var ids = destinationIds.ToHashSet(StringComparer.Ordinal);
        foreach (var pair in states) if (ids.Any(id => pair.Key.StartsWith(id + "|", StringComparison.Ordinal))) pair.Value.Stale = true;
        Raise(nameof(CacheStatus));
    }
    public void Cancel() { SummariesLoad.Cancel(); FilesLoad.Cancel(); query?.Cancel(); }

    private sealed class BrowserState
    {
        public List<SnapshotItem> All = [];
        public DateTimeOffset? LoadedAt;
        public bool Stale = true, Visited;
        public string? SelectedId, FilePath;
        public string Folder = "", Search = "", Series = "", Coverage = "All coverage", Status = "All statuses";
        public DateTime? From, Through;
        public int Page;
        public double SnapshotScroll, FileScroll;
    }
}

public sealed record RecoverySeriesOption(string Id, string Label);
