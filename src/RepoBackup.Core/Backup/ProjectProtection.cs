using RepoBackup.Core.Models;

namespace RepoBackup.Core.Backup;

public sealed record ProjectProtection(JobRecord? LatestAttempt, DateTimeOffset? LastBackup)
{
    public bool NeedsBackup => LastBackup is null || LatestAttempt is { } last && !RunOutcome.HasRecoveryPoint(last);
    public static ProjectProtection For(string projectId, string? destinationId, IEnumerable<JobRecord> history)
    {
        var jobs = history.Where(j => j.ProjectId == projectId && j.DestinationId == destinationId && j.Coverage == Coverage.FullProject)
            .OrderByDescending(j => j.StartedAt).ToList();
        return new(jobs.FirstOrDefault(), jobs.FirstOrDefault(RunOutcome.HasRecoveryPoint)?.FinishedAt);
    }
}
