using RepoBackup.Core.Backup;
using RepoBackup.Core.Models;

namespace RepoBackup.Desktop.Infrastructure;

public sealed class DesktopOperationCoordinator
{
    private readonly SemaphoreSlim repository = new(1, 1);
    private readonly object gate = new();
    private readonly HashSet<CancellationTokenSource> reads = [];
    public void CancelReads() { lock (gate) foreach (var read in reads) read.Cancel(); }
    public async Task<T> ReadAsync<T>(Func<CancellationToken, Task<T>> read, Func<bool> allowed, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        lock (gate) reads.Add(cancellation);
        try
        {
            using var lease = await EnterAsync(cancellation.Token);
            if (!allowed()) throw new OperationCanceledException(cancellation.Token);
            return await read(cancellation.Token);
        }
        finally { lock (gate) reads.Remove(cancellation); }
    }
    public async Task<IDisposable> EnterAsync(CancellationToken token)
    {
        await repository.WaitAsync(token); return new Lease(repository);
    }
    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) semaphore.Release(); }
    }
}

internal sealed class CoordinatedBackupBackend(IBackupQueueBackend inner, DesktopOperationCoordinator operations) : IBackupQueueBackend
{
    public JobRecord RecordFailure(BackupRequest request, Exception error) => inner.RecordFailure(request, error);
    public async Task<IBackupRunSession> OpenRunAsync(Destination destination, CancellationToken token)
    {
        operations.CancelReads();
        var lease = await operations.EnterAsync(token);
        try { return new Session(await inner.OpenRunAsync(destination, token), lease); }
        catch { lease.Dispose(); throw; }
    }
    private sealed class Session(IBackupRunSession inner, IDisposable lease) : IBackupRunSession
    {
        public Task<JobRecord> BackupAsync(BackupRequest request, IProgress<BackupProgress>? progress, CancellationToken token) => inner.BackupAsync(request, progress, token);
        public Task<IReadOnlyList<JobRecord>> CompleteAsync(IProgress<BackupProgress>? progress, CancellationToken token) => inner.CompleteAsync(progress, token);
        public void Dispose() { try { inner.Dispose(); } finally { lease.Dispose(); } }
    }
}
