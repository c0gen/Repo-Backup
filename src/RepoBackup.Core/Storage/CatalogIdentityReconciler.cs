using RepoBackup.Core.Backup;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Storage;

internal static class CatalogIdentityReconciler
{
    public static List<ProjectEntry> Reconcile(IEnumerable<ProjectEntry> projects, IReadOnlyList<JobRecord> history, IReadOnlyList<SavedSelection> selections)
    {
        var result = new List<ProjectEntry>();
        foreach (var group in projects.Select(ProjectIdentity.Normalize).GroupBy(RootKey, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group.OrderByDescending(p => history.Any(j => Owns(p, j.ProjectId) && RunOutcome.HasRecoveryPoint(j)))
                .ThenByDescending(p => history.Any(j => Owns(p, j.ProjectId) && j.SnapshotId is not null))
                .ThenByDescending(p => p.Reviewed && p.Enabled).ThenByDescending(p => p.Reviewed)
                .ThenBy(p => p.DiscoveredAt).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();
            if (ordered.Any(p => !SameRules(p.Exclusions, ordered[0].Exclusions)))
            { result.AddRange(ordered.Select(p => p with { IdentityConflict = true })); continue; }
            var survivor = ordered[0];
            foreach (var duplicate in ordered.Skip(1)) survivor = Merge(survivor, duplicate, equivalent: true);
            result.Add(survivor with { IdentityConflict = false });
        }
        // A never-reviewed discovery of one root can be absorbed into an existing multi-root project.
        foreach (var candidate in result.Where(p => !p.Reviewed && !p.Enabled && !p.IdentityConflict).ToList())
        {
            if (history.Any(j => Owns(candidate, j.ProjectId)) || selections.Any(s => Owns(candidate, s.ProjectId))) continue;
            var owners = result.Where(p => p.Id != candidate.Id && p.Roots.Count > candidate.Roots.Count
                && SameRules(p.Exclusions, candidate.Exclusions) && candidate.Roots.All(r => p.Roots.Any(o => o.Path.Equals(r.Path, StringComparison.OrdinalIgnoreCase)))).ToList();
            if (owners.Count != 1) continue;
            var index = result.FindIndex(p => p.Id == owners[0].Id);
            result[index] = Merge(owners[0], candidate, equivalent: false); result.Remove(candidate);
        }
        for (var i = 0; i < result.Count; i++)
            if (result.Any(p => p.Id != result[i].Id && p.Roots.Any(r => result[i].Roots.Any(o => o.Path.Equals(r.Path, StringComparison.OrdinalIgnoreCase)))))
                result[i] = result[i] with { IdentityConflict = true };
        var ids = result.SelectMany(p => p.LegacyIds.Prepend(p.Id)).ToList();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count) throw new InvalidDataException("Conflicting legacy project identities.");
        return result;
    }

    public static bool Owns(ProjectEntry project, string id) => project.Id == id || project.LegacyIds.Contains(id, StringComparer.Ordinal);
    public static string RootKey(ProjectEntry project) => string.Join('\0', project.Roots.Select(r => PathSafety.Normalize(r.Path)).Order(StringComparer.OrdinalIgnoreCase));
    public static bool SameRules(ExclusionRules a, ExclusionRules b) => a.ExcludedDirectories.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b.ExcludedDirectories)
        && a.RelativePatterns.Select(p => p.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b.RelativePatterns.Select(p => p.Replace('\\', '/')));

    public static ProjectEntry Merge(ProjectEntry survivor, ProjectEntry duplicate, bool equivalent) => survivor with
    {
        LegacyIds = (equivalent ? survivor.LegacyIds.Concat(duplicate.LegacyIds).Append(duplicate.Id) : survivor.LegacyIds).Distinct().Where(id => id != survivor.Id).ToList(),
        Roots = survivor.Roots.Select(root => duplicate.Roots.FirstOrDefault(r => r.Path.Equals(root.Path, StringComparison.OrdinalIgnoreCase)) is { } other
            ? ProjectIdentity.Observe(root with { LegacyIds = root.LegacyIds.Concat(other.LegacyIds).Append(other.Id).Where(id => id != root.Id).Distinct().ToList(),
                PreviousPaths = root.PreviousPaths.Concat(other.PreviousPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToList() }, other.Aliases)
            : root).ToList(),
        DiscoverySources = survivor.DiscoverySources.Concat(duplicate.DiscoverySources).Distinct().ToList(),
        Reviewed = survivor.Reviewed || duplicate.Reviewed, Enabled = survivor.Enabled || duplicate.Enabled,
        Dismissed = survivor.Dismissed && duplicate.Dismissed, DiscoveredAt = survivor.DiscoveredAt < duplicate.DiscoveredAt ? survivor.DiscoveredAt : duplicate.DiscoveredAt
    };
}
