using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Storage;

public sealed partial class CatalogStore
{
    public int RecoverCatalog(IReadOnlyList<RecoveryManifest> manifests, IReadOnlyList<JobRecord> history)
    {
        lock (writeGate)
        {
            using var db = Open(); using var transaction = db.BeginTransaction(deferred: false);
            var existing = ReadAll<ProjectEntry>(db, "projects", transaction);
            var combined = existing.ToList(); var added = 0;
            // Process successor identities first, including when an older snapshot used a previous root path.
            foreach (var candidate in manifests.GroupBy(m => m.Project.Id).Select(g => g.First().Project).OrderByDescending(p => p.LegacyIds.Count))
            {
                if (combined.Any(p => CatalogIdentityReconciler.Owns(p, candidate.Id))) continue;
                var local = combined.FindIndex(p => candidate.LegacyIds.Any(id => CatalogIdentityReconciler.Owns(p, id)));
                if (local >= 0)
                {
                    var owner = combined[local];
                    combined[local] = owner with { LegacyIds = owner.LegacyIds.Concat(candidate.LegacyIds).Append(candidate.Id)
                        .Where(id => id != owner.Id).Distinct().ToList() };
                }
                else { combined.Add(PrepareSavedProject(candidate with { Reviewed = true, Enabled = false }, null)); added++; }
            }
            var selections = ReadAll<SavedSelection>(db, "selections", transaction);
            var recovered = manifests.Where(m => m.Selection is not null).DistinctBy(m => m.Selection!.Id)
                .Where(m => selections.All(s => s.Id != m.Selection!.Id)).Select(m =>
                {
                    if (m.Selection!.ProjectId != m.Project.Id || m.Selection.Paths.Count == 0)
                        throw new InvalidDataException("Recovered selection does not belong to its recorded project.");
                    var project = combined.Single(p => CatalogIdentityReconciler.Owns(p, m.Project.Id));
                    var paths = m.Selection!.Paths.Select(path =>
                    {
                        var root = m.Project.Roots.OrderByDescending(r => r.Path.Length).FirstOrDefault(r => PathSafety.IsWithin(path, r.Path))
                            ?? throw new InvalidDataException("Recovered selection is outside its project roots.");
                        var current = project.Roots.SingleOrDefault(r => r.Id == root.Id || r.LegacyIds.Contains(root.Id) || root.LegacyIds.Contains(r.Id) || r.Path.Equals(PathSafety.Normalize(root.Path), StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidDataException("Recovered selection's root identity no longer exists locally.");
                        return Path.Combine(current.Path, Path.GetRelativePath(PathSafety.Normalize(root.Path), PathSafety.Normalize(path)));
                    }).ToList();
                    return m.Selection with { ProjectId = project.Id, Paths = paths };
                }).ToList();
            // Validate everything before writing; preserve local projects, selections and jobs.
            foreach (var selection in recovered) Upsert(db, transaction, "selections", selection.Id, selection);
            var existingJobs = ReadAll<JobRecord>(db, "history", transaction).Select(j => j.Id).ToHashSet();
            foreach (var job in history.Where(j => !existingJobs.Contains(j.Id))) Upsert(db, transaction, "history", job.Id, job);
            ReconcileIdentities(db, transaction, combined, validateOwnership: true);
            transaction.Commit(); return added;
        }
    }
}
