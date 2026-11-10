using System.Collections.Concurrent;
using RepoBackup.Core.Backup;
using RepoBackup.Core.Models;

namespace RepoBackup.Tests;

internal sealed class ControlledBackupBackend : IBackupQueueBackend
{
    public ConcurrentQueue<BackupRequest> Started { get; } = new();
    public ConcurrentDictionary<string, JobRecord> Results { get; } = new();
    public Func<Destination, CancellationToken, Task>? OnOpen { get; set; }
    public Func<BackupRequest, IProgress<BackupProgress>?, CancellationToken, Task>? OnBackup { get; set; }
    public Func<IProgress<BackupProgress>?, CancellationToken, Task>? OnComplete { get; set; }
    private int sessions, openCount, completeCount, maximumSessions;
    public int OpenCount => openCount;
    public int CompleteCount => completeCount;
    public int MaximumSessions => maximumSessions;

    public async Task<IBackupRunSession> OpenRunAsync(Destination destination, CancellationToken token)
    {
        Interlocked.Increment(ref openCount);
        if (OnOpen is { } open) await open(destination, token);
        token.ThrowIfCancellationRequested();
        var active = Interlocked.Increment(ref sessions);
        maximumSessions = Math.Max(maximumSessions, active);
        return new Session(this);
    }

    public JobRecord RecordFailure(BackupRequest request, Exception error)
    {
        var outcome = error is OperationCanceledException ? Outcome.Cancelled : Outcome.Failed;
        var job = Results.TryGetValue(request.Id, out var current) ? current : Job(request);
        job = job with { Backup = outcome, FinishedAt = DateTimeOffset.UtcNow, Messages = [error.Message] };
        Results[request.Id] = job; return job;
    }

    private static JobRecord Job(BackupRequest request) => new()
    {
        Id = request.Id, ProjectId = request.Project.Id, ProjectName = request.Name,
        SeriesId = request.SeriesId, DestinationId = request.Destination.Id
    };

    private sealed class Session(ControlledBackupBackend owner) : IBackupRunSession
    {
        private readonly List<JobRecord> jobs = [];
        public async Task<JobRecord> BackupAsync(BackupRequest request, IProgress<BackupProgress>? progress, CancellationToken token)
        {
            owner.Started.Enqueue(request);
            JobRecord job;
            try
            {
                token.ThrowIfCancellationRequested();
                if (owner.OnBackup is { } backup) await backup(request, progress, token);
                job = Job(request) with { SnapshotId = request.Id, Verification = Outcome.Running };
                owner.Results[request.Id] = job;
            }
            catch (Exception error) { job = owner.RecordFailure(request, error); }
            jobs.Add(job); return job;
        }
        public async Task<IReadOnlyList<JobRecord>> CompleteAsync(IProgress<BackupProgress>? progress, CancellationToken token)
        {
            Interlocked.Increment(ref owner.completeCount);
            if (owner.OnComplete is { } complete) await complete(progress, token);
            token.ThrowIfCancellationRequested();
            for (var i = 0; i < jobs.Count; i++)
            {
                if (jobs[i].FinishedAt is null)
                    jobs[i] = jobs[i] with { Backup = Outcome.Successful, Verification = Outcome.Successful, Cleanup = Outcome.Successful, FinishedAt = DateTimeOffset.UtcNow };
                owner.Results[jobs[i].Id] = jobs[i];
            }
            return jobs.ToArray();
        }
        public void Dispose() => Interlocked.Decrement(ref owner.sessions);
    }

    public static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
