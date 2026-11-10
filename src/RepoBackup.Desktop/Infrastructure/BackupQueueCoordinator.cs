using RepoBackup.Core.Backup;
using RepoBackup.Core.Models;
using RepoBackup.Desktop.ViewModels;

namespace RepoBackup.Desktop.Infrastructure;

// Owns execution and cancellation; no worker callback touches WPF collections or controls.
public sealed class BackupQueueCoordinator(IBackupQueueBackend backend)
{
    private readonly object gate = new();
    private readonly List<BackupQueueEntry> entries = [];
    private bool running, paused, finalizing;
    private long version;
    private string? currentId, runId;
    private CancellationTokenSource? runCancellation, projectCancellation;
    private Task completion = Task.CompletedTask;
    public event Action<BackupQueueSnapshot>? Changed;
    public event Action<BackupProgress>? ProgressReported;
    public event Action<IReadOnlyList<JobRecord>>? ResultsChanged;
    public event Action<Exception>? Faulted;
    public BackupQueueSnapshot Snapshot { get { lock (gate) return BuildSnapshot(); } }

    public (int Added, int Duplicates) Enqueue(IEnumerable<BackupRequest> requests)
    {
        lock (gate)
        {
            var added = 0; var duplicates = 0;
            foreach (var request in requests)
            {
                if (entries.Any(e => e.Request.DuplicateKey == request.DuplicateKey)) { duplicates++; continue; }
                entries.Add(new(request, BackupQueueState.Waiting)); added++;
            }
            StartWorker(); Publish(); return (added, duplicates);
        }
    }

    public void Pause() { lock (gate) { paused = true; Publish(); } }
    public void Resume() { lock (gate) { paused = false; StartWorker(); Publish(); } }
    public void CancelCurrent(string? expectedRequestId = null)
    {
        lock (gate)
        {
            // A click rendered for the previous project must not cancel its successor.
            if (expectedRequestId is not null && expectedRequestId != currentId) return;
            projectCancellation?.Cancel(); Publish();
        }
    }
    public void CancelAll()
    {
        lock (gate)
        {
            entries.RemoveAll(e => e.State == BackupQueueState.Waiting);
            runCancellation?.Cancel(); Publish();
        }
    }

    public void Remove(string id)
    {
        lock (gate) { entries.RemoveAll(e => e.Request.Id == id && e.State == BackupQueueState.Waiting); Publish(); }
    }
    public bool CanMove(string id, int direction)
    {
        lock (gate)
        {
            var waiting = entries.Where(e => e.State == BackupQueueState.Waiting).ToList();
            var index = waiting.FindIndex(e => e.Request.Id == id);
            return index >= 0 && index + direction >= 0 && index + direction < waiting.Count;
        }
    }
    public void Move(string id, int direction)
    {
        lock (gate)
        {
            if (!CanMove(id, direction)) return;
            var indices = Enumerable.Range(0, entries.Count).Where(i => entries[i].State == BackupQueueState.Waiting).ToList();
            var index = indices.FindIndex(i => entries[i].Request.Id == id);
            (entries[indices[index]], entries[indices[index + direction]]) = (entries[indices[index + direction]], entries[indices[index]]);
            Publish();
        }
    }

    public bool Accepts(BackupProgress progress)
    {
        lock (gate) return running && progress.RunId == runId && progress.RequestId == currentId
            && (currentId is not null || finalizing);
    }

    public async Task WaitForStopAsync()
    {
        while (true)
        {
            Task pending;
            lock (gate) { if (!running) return; pending = completion; }
            await pending;
        }
    }

    private void StartWorker()
    {
        if (running || paused || !entries.Any(e => e.State == BackupQueueState.Waiting)) return;
        running = true;
        var cancellation = new CancellationTokenSource();
        runCancellation = cancellation;
        completion = Task.Run(() => PumpAsync(cancellation));
    }

    private async Task PumpAsync(CancellationTokenSource cancellation)
    {
        try
        {
            while (TakeNext(null, cancellation) is { } first)
            {
                var owner = runId;
                IBackupRunSession? session = null;
                var segment = new List<BackupRequest>();
                try
                {
                    Report(first.Request, new(first.Request.Name, "Preparing destination", IsIndeterminate: true), owner);
                    session = await backend.OpenRunAsync(first.Request.Destination, first.Cancellation.Token);
                    var next = first;
                    while (true)
                    {
                        var request = next.Request;
                        segment.Add(request);
                        JobRecord result;
                        try { result = await session.BackupAsync(request, new Callback(p => Report(request, p, owner)), next.Cancellation.Token); }
                        catch (Exception error) { result = backend.RecordFailure(request, error); }
                        var stopSegment = next.Cancellation.IsCancellationRequested;
                        FinishProject(request, result);
                        next.Cancellation.Dispose();
                        if (stopSegment || TakeNext(request.Destination, cancellation) is not { } following) break;
                        next = following;
                    }
                    lock (gate) { finalizing = true; Publish(); }
                    ProgressReported?.Invoke(new("", "Finalizing queued backups", IsIndeterminate: true, RunId: owner));
                    var results = await session.CompleteAsync(new Callback(p => Report(null, p, owner)), cancellation.Token);
                    FinishSegment(segment, results);
                }
                catch (Exception error)
                {
                    // Include preparation failures, before a session or snapshot exists.
                    if (segment.Count == 0) segment.Add(first.Request);
                    var results = segment.Select(request => backend.RecordFailure(request, error)).ToArray();
                    FinishSegment(segment, results);
                }
                finally
                {
                    session?.Dispose();
                    lock (gate)
                    {
                        projectCancellation?.Dispose(); projectCancellation = null;
                        currentId = null; finalizing = false; runId = null; Publish();
                    }
                }
            }
        }
        catch (Exception error)
        {
            lock (gate)
            {
                entries.RemoveAll(e => e.State != BackupQueueState.Waiting);
                paused = true;
            }
            Faulted?.Invoke(error);
        }
        finally
        {
            lock (gate)
            {
                projectCancellation?.Dispose(); projectCancellation = null;
                runCancellation = null; cancellation.Dispose();
                currentId = runId = null; finalizing = running = false;
                StartWorker(); Publish();
            }
        }
    }

    private (BackupRequest Request, CancellationTokenSource Cancellation)? TakeNext(Destination? destination, CancellationTokenSource cancellation)
    {
        lock (gate)
        {
            if (paused || cancellation.IsCancellationRequested) return null;
            var index = entries.FindIndex(e => e.State == BackupQueueState.Waiting);
            if (index < 0 || destination is not null && entries[index].Request.Destination != destination) return null;
            if (destination is null) runId = Guid.NewGuid().ToString("N");
            var entry = entries[index];
            entries[index] = entry with { State = BackupQueueState.Active };
            currentId = entry.Request.Id;
            projectCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            Publish(); return (entry.Request, projectCancellation);
        }
    }

    private void FinishProject(BackupRequest request, JobRecord result)
    {
        lock (gate)
        {
            var index = entries.FindIndex(e => e.Request.Id == request.Id);
            if (index >= 0)
            {
                if (result.FinishedAt is null) entries[index] = entries[index] with { State = BackupQueueState.AwaitingVerification };
                else entries.RemoveAt(index);
            }
            projectCancellation = null; currentId = null; Publish();
        }
        if (result.FinishedAt is not null) ResultsChanged?.Invoke([result]);
    }

    private void FinishSegment(List<BackupRequest> requests, IReadOnlyList<JobRecord> results)
    {
        lock (gate)
        {
            var ids = requests.Select(r => r.Id).ToHashSet();
            entries.RemoveAll(e => ids.Contains(e.Request.Id)); Publish();
        }
        ResultsChanged?.Invoke(results);
    }

    private void Report(BackupRequest? request, BackupProgress progress, string? owner)
    {
        ProgressReported?.Invoke(progress with { RequestId = request?.Id, RunId = owner });
    }

    private BackupQueueSnapshot BuildSnapshot() => new(version, entries.ToArray(), running, paused, finalizing, currentId, runId,
        projectCancellation is { IsCancellationRequested: false } && !finalizing);
    private void Publish() { version++; Changed?.Invoke(BuildSnapshot()); }
    private sealed class Callback(Action<BackupProgress> report) : IProgress<BackupProgress>
    { public void Report(BackupProgress value) => report(value); }
}
