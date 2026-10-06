using RepoBackup.Core.Models;

namespace RepoBackup.Core.Backup;

public static class RunOutcome
{
    public static bool HasRecoveryPoint(JobRecord job) => job.Backup == Outcome.Successful && job.Verification == Outcome.Successful;
    public static bool IsSuccessful(JobRecord job) => HasRecoveryPoint(job) && job.Cleanup is Outcome.Successful or Outcome.NotRun;
    public static bool IsCancelled(JobRecord job) => job.Backup == Outcome.Cancelled || job.Verification == Outcome.Cancelled || job.Cleanup == Outcome.Cancelled;
    public static int ExitCode(IEnumerable<JobRecord> results)
    {
        var jobs = results.ToList();
        return jobs.Any(IsCancelled) ? 130 : jobs.Count > 0 && jobs.All(IsSuccessful) ? 0 : 2;
    }
}
