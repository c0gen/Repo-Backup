using RepoBackup.Core.Infrastructure;

namespace RepoBackup.Core.Backup;

public sealed class DestinationLease : IDisposable
{
    private readonly FileStream local;
    private readonly FileStream? remote;
    private DestinationLease(FileStream local, FileStream? remote) { this.local = local; this.remote = remote; }
    public static DestinationLease Acquire(AppPaths paths, string destination)
    {
        var lockPath = Path.Combine(paths.LocksDirectory, PathSafety.LockKey(destination) + ".lock");
        FileStream? local = null;
        try
        {
            local = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            // A share-level lease also serializes different catalogs/machines against UNC repositories.
            var remote = Directory.Exists(destination) ? new FileStream(Path.Combine(destination, ".repobackup-operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None) : null;
            return new(local, remote);
        }
        catch (IOException e) { local?.Dispose(); throw new IOException("This destination is unavailable or another operation is already using it.", e); }
    }
    public void Dispose() { remote?.Dispose(); local.Dispose(); }
}
