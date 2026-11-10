using System.Security.Principal;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using RepoBackup.Core.Storage;

namespace RepoBackup.Core.Backup;

public sealed partial class BackupService
{
    public async Task<IBackupRunSession> OpenRunAsync(Destination destination, CancellationToken token)
    {
        if (destination.IsRetired || catalog.Destinations().Any(d => d.Id == destination.Id && d.IsRetired))
            throw new InvalidOperationException("This destination was replaced. Choose an active destination.");
        token.ThrowIfCancellationRequested();
        var lease = DestinationLease.Acquire(paths, destination.Path);
        try
        {
            await restic.PrepareRepositoryAsync(destination, token);
            var snapshots = await restic.SnapshotSummariesAsync(destination, token);
            return new BackupRunSession(this, destination, lease, snapshots, catalog, paths);
        }
        catch { lease.Dispose(); throw; }
    }

    public JobRecord RecordFailure(BackupRequest request, Exception error)
    {
        var job = Failed(catalog.Job(request.Id) ?? NewJob(request.Project, request.Destination, request.Selection) with { Id = request.Id }, error);
        catalog.SaveJob(job);
        return job;
    }

    private sealed class BackupRunSession(BackupService owner, Destination destination, DestinationLease lease,
        List<SnapshotSummary> snapshots, CatalogStore catalog, AppPaths paths) : IBackupRunSession
    {
        private readonly List<JobRecord> jobs = [];
        private bool completed, disposed;

        public async Task<JobRecord> BackupAsync(BackupRequest request, IProgress<BackupProgress>? progress, CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (completed) throw new InvalidOperationException("This backup run has already finished.");
            var owner = catalog.Projects().SingleOrDefault(p => p.Id == request.Project.Id || p.LegacyIds.Contains(request.Project.Id));
            if (owner is not null && owner.Id != request.Project.Id)
                request = request with { Project = request.Project with { Id = owner.Id, LegacyIds = [.. owner.LegacyIds] },
                    Selection = request.Selection is { } captured ? captured with { ProjectId = owner.Id } : null };
            var job = NewJob(request.Project, destination, request.Selection) with { Id = request.Id };
            try
            {
                if (request.Destination != destination) throw new ArgumentException("The request belongs to another destination run.");
                if (!Enum.IsDefined(request.ReadMode)) throw new ArgumentException("Unsupported backup read mode.");
                if (request.UseVss && !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new InvalidOperationException("VSS was selected. Run Repo Backup as Administrator to use volume snapshots; the run has not fallen back to live mode.");
                var current = catalog.Projects().SingleOrDefault(p => p.Id == request.Project.Id);
                if (current is null || !current.Reviewed || current.Dismissed)
                    throw new InvalidOperationException("The queued project is no longer available for backup. Review it and queue it again.");
                if (request.Selection is { } selection && selection.ProjectId != request.Project.Id)
                    throw new ArgumentException("A selection must belong to the requested project.");
            }
            catch (Exception error) when (IsBackupError(error))
            {
                job = Failed(job, error); catalog.SaveJob(job); jobs.Add(job); return job;
            }
            return await BackupProjectAsync(request.Project, request.Selection, job, request.ReadMode, request.UseVss, progress, token);
        }

        public async Task<JobRecord> BackupProjectAsync(ProjectEntry project, SavedSelection? selection, JobRecord job,
            BackupReadMode mode, bool useVss, IProgress<BackupProgress>? progress, CancellationToken token)
        {
            catalog.SaveJob(job);
            try
            {
                token.ThrowIfCancellationRequested();
                job = await owner.CreateSnapshotAsync(destination, project, selection, job, snapshots, mode, useVss, progress, token);
            }
            catch (Exception error) when (IsBackupError(error)) { job = Failed(catalog.Job(job.Id) ?? job, error); }
            finally
            {
                if (BackupStaging.Clean(paths, job.Id) is { } warning) job = job with { Messages = [.. job.Messages, warning] };
            }
            catalog.SaveJob(job); jobs.Add(job);
            progress?.Report(new(job.ProjectName, job.FinishedAt is null ? "Awaiting verification" : job.Backup.ToString(), IsIndeterminate: true));
            return job;
        }

        public async Task<IReadOnlyList<JobRecord>> CompleteAsync(IProgress<BackupProgress>? progress, CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!completed)
            {
                completed = true;
                await owner.FinalizeBatchAsync(destination, jobs, progress, token);
            }
            return jobs.ToArray();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            lease.Dispose();
        }
    }
}
