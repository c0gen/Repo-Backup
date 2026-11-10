using RepoBackup.Core.Models;

namespace RepoBackup.Core.Backup;

public static class SnapshotRetention
{
    public static List<string> Obsolete(IEnumerable<SnapshotSummary> snapshots, IEnumerable<JobRecord> verifiedJobs,
        IEnumerable<ProjectEntry> projects)
    {
        var aliases = projects.SelectMany(p => p.LegacyIds.Prepend(p.Id)
            .Select(id => (Legacy: "project-" + id, Canonical: "project-" + p.Id)))
            .ToDictionary(p => p.Legacy, p => p.Canonical, StringComparer.Ordinal);
        string Canonical(string series) => aliases.GetValueOrDefault(series, series);
        var series = verifiedJobs.Where(j => j.Backup == Outcome.Successful && j.Verification == Outcome.Successful)
            .Select(j => (Canonical(j.SeriesId), j.Coverage)).ToHashSet();
        return snapshots.Where(s => s.Successful && !s.Tags.Contains("pending")
                && series.Contains((Canonical(s.TagValue("series") ?? ""), s.Coverage)))
            .GroupBy(s => (Canonical(s.TagValue("series") ?? ""), s.Coverage))
            .SelectMany(g => g.OrderByDescending(s => s.Time).ThenBy(s => s.Id, StringComparer.Ordinal).Skip(10))
            .Select(s => s.Id).ToList();
    }
}
