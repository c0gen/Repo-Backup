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

    public sealed class Request : IDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        public Request(string identity) { Identity = identity; Token = cancellation.Token; }
        public string Identity { get; }
        public CancellationToken Token { get; }
        private bool disposed;
        public void Cancel() { if (!disposed) cancellation.Cancel(); }
        public void Dispose() { disposed = true; cancellation.Dispose(); }
    }
}
