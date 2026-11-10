using System.Windows.Input;

namespace RepoBackup.Desktop.ViewModels;

public sealed partial class SnapshotBrowserViewModel
{
    public const int PageSize = 200;
    private readonly LinkedList<(string Key, SnapshotContentIndex Index)> cache = new();
    private SnapshotContentIndex? index;
    private SnapshotFileItem[] matches = [];
    private CancellationTokenSource? query;
    public string Search { get => state.Search; set { if (state.Search == value) return; state.Search = value; state.Page = 0; state.FileScroll = 0; Raise(); _ = QueryAsync(true); } }
    public string FolderLabel => index?.Find(state.Folder)?.DisplayPath ?? "All sources";
    public string PageLabel => matches.Length == 0 ? "0 items" : $"Page {state.Page + 1} of {(matches.Length + PageSize - 1) / PageSize} · {matches.Length:N0} items";
    public int CacheCount => cache.Count;

    public async Task LoadFilesAsync()
    {
        var target = destination; var snapshot = selected;
        using var request = FilesLoad.Begin(identity + "|" + snapshot?.Snapshot.Id);
        query?.Cancel(); index = null; matches = []; selectedFile = null; Files.Replace([]); Raise(nameof(SelectedFile));
        if (target is null || snapshot is null) { FilesLoad.Cancel(request); RaiseFileState(); return; }
        var key = identity + "|" + snapshot.Snapshot.Id;
        try
        {
            var cached = cache.FirstOrDefault(x => x.Key == key);
            if (cached.Index is not null)
            {
                cache.Remove(cached); cache.AddFirst(cached); index = cached.Index;
            }
            else
            {
                if (!canRead()) { Notice = "Pause the backup queue to load this backup’s files. Already loaded backups can still be browsed."; FilesLoad.Cancel(request); return; }
                var files = await FileLoader(target, snapshot.Snapshot, request.Token);
                var loaded = await Task.Run(() => SnapshotContentIndex.Build(files, request.Token), request.Token);
                if (!FilesLoad.Accepts(request)) return;
                index = loaded; cache.AddFirst((key, loaded));
                while (cache.Count > 2) cache.RemoveLast();
            }
            if (!FilesLoad.Accepts(request)) return;
            if (index.Find(state.Folder)?.File.Type != "dir") state.Folder = "";
            await QueryAsync(false);
            FilesLoad.Complete(request); RaiseFileState();
        }
        catch (OperationCanceledException) { FilesLoad.Cancel(request); }
        catch (Exception error) { FilesLoad.Fail(request, error); }
    }

    public async Task QueryAsync(bool debounce = false)
    {
        query?.Cancel();
        using var cancellation = new CancellationTokenSource(); query = cancellation;
        var current = index; var folder = state.Folder; var search = state.Search;
        if (current is null) { matches = []; PublishPage(); return; }
        try
        {
            if (debounce) await Task.Delay(200, cancellation.Token);
            var result = await Task.Run(() => current.Query(folder, search, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested || index != current) return;
            matches = result; PublishPage();
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(query, cancellation)) query = null; }
    }

    private void PublishPage()
    {
        state.Page = Math.Clamp(state.Page, 0, Math.Max(0, (matches.Length - 1) / PageSize));
        publishing = true;
        try
        {
            Files.Replace(matches.Skip(state.Page * PageSize).Take(PageSize));
            selectedFile = Files.FirstOrDefault(x => x.Path == state.FilePath);
            if (selectedFile is null) state.FilePath = null;
        }
        finally { publishing = false; }
        Raise(nameof(SelectedFile)); RaiseFileState(); CommandManager.InvalidateRequerySuggested();
    }
    private void ChangePage(int delta) { state.Page += delta; state.FileScroll = 0; PublishPage(); }
    private void NavigateFolder(string path)
    {
        state.Folder = path; state.Page = 0; state.FilePath = null; state.Search = ""; state.FileScroll = 0;
        Raise(nameof(Search)); _ = QueryAsync();
    }
    private string ParentFolder()
    {
        var current = index?.Find(state.Folder);
        return current is not null && current.DisplayPath.Contains('/') ? state.Folder[..state.Folder.LastIndexOf('/')] : "";
    }
    private void RaiseFileState() { Raise(nameof(Search)); Raise(nameof(FolderLabel)); Raise(nameof(PageLabel)); }
}
