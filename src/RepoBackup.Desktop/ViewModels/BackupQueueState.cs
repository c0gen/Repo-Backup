using RepoBackup.Core.Models;
using RepoBackup.Desktop.Infrastructure;

namespace RepoBackup.Desktop.ViewModels;

public enum BackupQueueState { Waiting, Active, AwaitingVerification }
public sealed record BackupQueueEntry(BackupRequest Request, BackupQueueState State);
public sealed record BackupQueueSnapshot(long Version, IReadOnlyList<BackupQueueEntry> Entries, bool IsRunning,
    bool IsPaused, bool IsFinalizing, string? CurrentRequestId, string? RunId, bool CanCancelCurrent);

public sealed class BackupQueueItem(BackupRequest request) : ObservableObject
{
    public BackupRequest Request { get; } = request;
    public string Id => Request.Id;
    public string Name => Request.Name;
    public string Destination => Request.Destination.Name;
    public string Scope => Request.Selection is null ? "Full project" : "Saved selection";
    private BackupQueueState state;
    public BackupQueueState State
    {
        get => state;
        set { if (Set(ref state, value)) { Raise(nameof(Status)); Raise(nameof(IsWaiting)); } }
    }
    public bool IsWaiting => State == BackupQueueState.Waiting;
    public string Status => State switch
    { BackupQueueState.Active => "Backing up", BackupQueueState.AwaitingVerification => "Awaiting verification", _ => "Waiting" };
}
