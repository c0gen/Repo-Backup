namespace RepoBackup.Desktop.ViewModels;

public sealed class SnapshotRequestCoordinator
{
    private Request? current;
    public Request Begin(string identity)
    {
        Cancel(); return current = new Request(identity);
    }
    public void Cancel() { current?.Cancel(); current = null; }
    public bool Accepts(Request request, string identity) => ReferenceEquals(current, request) && !request.Token.IsCancellationRequested && request.Identity == identity;

    public sealed class Request(string identity) : IDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        public string Identity { get; } = identity;
        public CancellationToken Token => cancellation.Token;
        private bool disposed;
        public void Cancel() { if (!disposed) cancellation.Cancel(); }
        public void Dispose() { disposed = true; cancellation.Dispose(); }
    }
}
