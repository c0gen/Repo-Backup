using System.Windows.Input;
using RepoBackup.Desktop.Infrastructure;

namespace RepoBackup.Desktop.ViewModels;

/// <summary>A panel's latest request owns both its result and its loading/error state.</summary>
public sealed class LoadState : ObservableObject
{
    private readonly SnapshotRequestCoordinator requests = new();
    private bool loading, loaded;
    private string error = "";
    public bool IsLoading => loading;
    public bool HasLoaded => loaded;
    public string Error => error;
    public bool HasError => error.Length > 0;
    public bool IsReady => loaded && !loading && !HasError;

    public SnapshotRequestCoordinator.Request Begin(string identity = "")
    {
        var request = requests.Begin(identity);
        loading = true; error = ""; Changed();
        return request;
    }
    public bool Accepts(SnapshotRequestCoordinator.Request request) => requests.Accepts(request, request.Identity);
    public void Complete(SnapshotRequestCoordinator.Request request)
    {
        if (!Accepts(request)) return;
        loading = false; loaded = true; error = ""; Changed();
    }
    public void Fail(SnapshotRequestCoordinator.Request request, Exception exception)
    {
        if (!Accepts(request)) return;
        loading = false; error = exception.Message; Changed();
    }
    public void SetError(Exception exception)
    {
        requests.Cancel(); loading = false; error = exception.Message; Changed();
    }
    public void Cancel()
    {
        requests.Cancel(); loading = false; loaded = false; error = ""; Changed();
    }
    public void Cancel(SnapshotRequestCoordinator.Request request)
    {
        if (Accepts(request)) Cancel();
    }
    private void Changed()
    {
        foreach (var property in new[] { nameof(IsLoading), nameof(HasLoaded), nameof(Error), nameof(HasError), nameof(IsReady) }) Raise(property);
        CommandManager.InvalidateRequerySuggested();
    }
}
