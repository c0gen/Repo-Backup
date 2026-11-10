using System.Diagnostics;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Backup;

public sealed partial class BackupService
{
    private async Task FinalizeBatchAsync(Destination destination, List<JobRecord> jobs, IProgress<BackupProgress>? progress, CancellationToken token)
    {
        var pending = Enumerable.Range(0, jobs.Count).Where(i => jobs[i].FinishedAt is null && jobs[i].SnapshotId is not null).ToList();
        if (pending.Count == 0) return;
        var watch = Stopwatch.StartNew();
        try
        {
            token.ThrowIfCancellationRequested();
            progress?.Report(new("", "Verifying repository", IsIndeterminate: true));
            (await restic.RunAsync(destination, ["check"], token)).EnsureSuccess("Verify new backups");
            var elapsed = watch.Elapsed.TotalMilliseconds;
            foreach (var i in pending)
            {
                jobs[i].PhaseMilliseconds["verification"] = elapsed;
                Save(i, jobs[i] with { Verification = Outcome.Successful });
            }
        }
        catch (Exception error) when (IsBackupError(error))
        {
            foreach (var i in pending) Save(i, Failed(jobs[i], error));
            return;
        }

        // A tag command creates a new snapshot ID. Always resolve run identities after tagging,
        // even if cancellation/error arrives after restic has committed a tag update.
        var attempted = new HashSet<int>();
        foreach (var i in pending)
        {
            if (jobs[i].Backup != Outcome.Running) { Save(i, jobs[i] with { FinishedAt = DateTimeOffset.UtcNow }); continue; }
            try
            {
                progress?.Report(new(jobs[i].ProjectName, "Finalizing verified snapshot", IsIndeterminate: true));
                token.ThrowIfCancellationRequested();
                attempted.Add(i);
                (await restic.RunAsync(destination, ["tag", "--remove", "pending", "--add", "successful", jobs[i].SnapshotId!], token))
                    .EnsureSuccess("Mark verified backup successful");
            }
            catch (Exception error) when (IsBackupError(error)) { Save(i, Failed(jobs[i], error)); }
        }

        List<SnapshotSummary> snapshots;
        try
        {
            // Read-only reconciliation is bounded and independent of the cancelled operation.
            using var reconcile = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            snapshots = await restic.SnapshotSummariesAsync(destination, reconcile.Token);
            foreach (var i in pending.Where(i => attempted.Contains(i)))
            {
                var snapshot = snapshots.SingleOrDefault(s => s.TagValue("run") == jobs[i].Id && s.Successful && !s.Tags.Contains("pending"));
                if (snapshot is not null)
                    Save(i, jobs[i] with { Backup = Outcome.Successful, SnapshotId = snapshot.Id, FinishedAt = null });
                else if (jobs[i].Backup == Outcome.Running)
                    Save(i, Failed(jobs[i], new IOException("The verified snapshot could not be finalized.")));
            }
        }
        catch (Exception error) when (IsBackupError(error))
        {
            foreach (var i in pending.Where(i => jobs[i].Backup == Outcome.Running)) Save(i, Failed(jobs[i], error, "Snapshot finalization could not be reconciled: "));
            return;
        }

        var successful = pending.Where(i => jobs[i].Backup == Outcome.Successful).ToList();
        if (successful.Count > 0)
            await CleanupBatchAsync(destination, jobs, successful, snapshots, progress, token);
        foreach (var i in pending)
        {
            if (jobs[i].FinishedAt is null) Save(i, jobs[i] with { FinishedAt = DateTimeOffset.UtcNow });
            progress?.Report(new(jobs[i].ProjectName, jobs[i].Backup.ToString(), 1, jobs[i].Files, jobs[i].Bytes));
        }

        void Save(int index, JobRecord job) { jobs[index] = job; catalog.SaveJob(job); }
    }

    private async Task CleanupBatchAsync(Destination destination, List<JobRecord> jobs, List<int> successful,
        List<SnapshotSummary> snapshots, IProgress<BackupProgress>? progress, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        var retryKey = "prunePending:" + (destination.RepositoryId ?? destination.Id);
        foreach (var i in successful) { jobs[i] = jobs[i] with { Cleanup = Outcome.Running }; catalog.SaveJob(jobs[i]); }
        try
        {
            progress?.Report(new("", "Keeping latest ten versions", IsIndeterminate: true));
            token.ThrowIfCancellationRequested();
            var obsolete = SnapshotRetention.Obsolete(snapshots, successful.Select(i => jobs[i]), catalog.Projects());
            if (obsolete.Count > 0)
            {
                // Save before forgetting: a killed process must not lose the need to reclaim data.
                catalog.SaveSetting(retryKey, true);
                (await restic.RunAsync(destination, new[] { "forget" }.Concat(obsolete), token)).EnsureSuccess("Remove obsolete snapshots");
            }
            if (catalog.GetSetting<bool>(retryKey))
            {
                progress?.Report(new("", "Reclaiming unused data", IsIndeterminate: true));
                (await restic.RunAsync(destination, ["prune"], token)).EnsureSuccess("Reclaim unused data");
                catalog.SaveSetting(retryKey, false);
            }
            foreach (var i in successful) jobs[i] = jobs[i] with { Cleanup = Outcome.Successful };
        }
        catch (Exception error) when (IsBackupError(error))
        {
            foreach (var i in successful) jobs[i] = Failed(jobs[i], error);
        }
        var elapsed = watch.Elapsed.TotalMilliseconds;
        foreach (var i in successful)
        {
            jobs[i].PhaseMilliseconds["cleanup"] = elapsed;
            catalog.SaveJob(jobs[i]);
        }
    }
}
