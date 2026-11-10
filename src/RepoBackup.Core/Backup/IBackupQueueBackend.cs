using RepoBackup.Core.Models;

namespace RepoBackup.Core.Backup;

public interface IBackupQueueBackend
{
    Task<IBackupRunSession> OpenRunAsync(Destination destination, CancellationToken token);
    JobRecord RecordFailure(BackupRequest request, Exception error);
}

public interface IBackupRunSession : IDisposable
{
    Task<JobRecord> BackupAsync(BackupRequest request, IProgress<BackupProgress>? progress, CancellationToken token);
    Task<IReadOnlyList<JobRecord>> CompleteAsync(IProgress<BackupProgress>? progress, CancellationToken token);
}
