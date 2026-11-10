using System.Security.Principal;
using RepoBackup.Core.Discovery;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using RepoBackup.Core.Storage;
using RepoBackup.Core.Windows;

namespace RepoBackup.Core.Backup;

public sealed partial class BackupService(AppPaths paths, CatalogStore catalog, CredentialStore credentials, ResticClient restic, SelectionPlanner planner, ProjectDiscoveryService discovery, Func<string, long?>? availableSpace = null) : IBackupQueueBackend
{
    public async Task<VerificationRecord> VerifyAsync(Destination destination, CancellationToken token = default)
    {
        var record = new VerificationRecord(destination.Id, DateTimeOffset.UtcNow, Outcome.Running, "Verification started");
        try
        {
            using var lease = DestinationLease.Acquire(paths, destination.Path);
            await restic.PrepareRepositoryAsync(destination, token);
            var result = await restic.RunAsync(destination, ["check", "--read-data"], token); result.EnsureSuccess("Verify repository data");
            record = record with { Outcome = Outcome.Successful, Message = "All repository data passed verification." };
            catalog.SaveDestination(destination with { VerifiedAt = record.At });
        }
        catch (OperationCanceledException) { record = record with { Outcome = Outcome.Cancelled, Message = "Verification cancelled." }; }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { record = record with { Outcome = Outcome.Failed, Message = e.Message }; }
        var history = catalog.GetSetting<List<VerificationRecord>>("verificationHistory") ?? [];
        catalog.SaveSetting("verificationHistory", history.Append(record).TakeLast(100).ToList()); return record;
    }

    public async Task<List<JobRecord>> RunAsync(Destination destination, IEnumerable<ProjectEntry> requestedProjects, SavedSelection? selection = null,
        IProgress<BackupProgress>? progress = null, CancellationToken token = default, bool useVss = false, BackupReadMode? readMode = null)
    {
        _ = discovery; // Constructor compatibility; discovery is independent of backup execution.
        var mode = readMode ?? catalog.GetSetting<BackupReadMode>(BackupReadPolicy.Setting);
        if (!Enum.IsDefined(mode)) throw new ArgumentException("Unsupported backup read mode.");
        if (useVss && !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) throw new InvalidOperationException("VSS was selected. Run Repo Backup as Administrator to use volume snapshots; the run has not fallen back to live mode.");
        if (destination.IsRetired || catalog.Destinations().Any(d => d.Id == destination.Id && d.IsRetired))
            throw new InvalidOperationException("This destination was replaced. Choose an active destination and reassign its schedules.");
        var current = catalog.Projects().SelectMany(p => p.LegacyIds.Prepend(p.Id).Select(id => (Id: id, Project: p))).ToDictionary(p => p.Id, p => p.Project);
        var projects = requestedProjects.Select(p => current.TryGetValue(p.Id, out var saved) ? saved : throw new ArgumentException("Unknown project: " + p.Id)).DistinctBy(p => p.Id).ToList();
        if (projects.Count == 0) throw new InvalidOperationException("Select at least one project.");
        if (projects.Any(p => !p.Reviewed || p.Dismissed)) throw new InvalidOperationException("Review discovered projects before including them in a backup.");
        if (selection is not null && (projects.Count != 1 || selection.ProjectId != projects[0].Id)) throw new ArgumentException("A selection must belong to the requested project.");
        var results = projects.Select(p => NewJob(p, destination, selection)).ToList();
        DestinationLease? acquired = null;
        List<SnapshotSummary> snapshots;
        try
        {
            token.ThrowIfCancellationRequested();
            acquired = DestinationLease.Acquire(paths, destination.Path);
            await restic.PrepareRepositoryAsync(destination, token);
            snapshots = await restic.SnapshotSummariesAsync(destination, token);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            acquired?.Dispose();
            for (var i = 0; i < results.Count; i++)
            {
                results[i] = Failed(results[i], e, "Destination preparation did not finish: "); catalog.SaveJob(results[i]);
            }
            return results;
        }
        catch { acquired?.Dispose(); throw; }
        using var session = new BackupRunSession(this, destination, acquired!, snapshots, catalog, paths);
        for (var i = 0; i < projects.Count; i++)
        {
            results[i] = await session.BackupProjectAsync(projects[i], selection, results[i], mode, useVss, progress, token);
        }
        return (await session.CompleteAsync(progress, token)).ToList();
    }

    private static JobRecord NewJob(ProjectEntry project, Destination destination, SavedSelection? selection) => new()
    {
        ProjectId = project.Id, ProjectName = selection?.Name ?? project.Name,
        SeriesId = selection is null ? "project-" + project.Id : "selection-" + selection.Id,
        DestinationId = destination.Id, Coverage = selection is null ? Coverage.FullProject : Coverage.PartialSelection
    };

    private static bool IsBackupError(Exception e) => e is IOException or InvalidDataException or UnauthorizedAccessException
        or System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException or OperationCanceledException or System.Text.Json.JsonException;

    private static JobRecord Failed(JobRecord job, Exception error, string prefix = "")
    {
        var outcome = error is OperationCanceledException ? Outcome.Cancelled : Outcome.Failed;
        return job with
        {
            Backup = job.Backup == Outcome.Running ? outcome : job.Backup,
            Verification = job.Verification == Outcome.Running ? outcome : job.Verification,
            Cleanup = job.Cleanup == Outcome.Running ? outcome : job.Cleanup,
            FinishedAt = DateTimeOffset.UtcNow, Messages = [.. job.Messages, prefix + error.Message]
        };
    }
}
