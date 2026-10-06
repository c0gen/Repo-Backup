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
            var projects = manifests.GroupBy(m => m.Project.Id).Select(g => g.First().Project)
                .Where(p => existing.All(e => e.Id != p.Id)).Select(p => PrepareSavedProject(p with { Reviewed = true, Enabled = false }, null)).ToList();
            var combined = existing.Concat(projects).ToList();
            foreach (var project in projects) ValidateOwnership(project, combined);
            var selections = ReadAll<SavedSelection>(db, "selections", transaction);
            var recovered = manifests.Where(m => m.Selection is not null).DistinctBy(m => m.Selection!.Id)
                .Where(m => selections.All(s => s.Id != m.Selection!.Id)).Select(m =>
                {
                    if (m.Selection!.ProjectId != m.Project.Id || m.Selection.Paths.Count == 0)
                        throw new InvalidDataException("Recovered selection does not belong to its recorded project.");
                    var project = combined.Single(p => p.Id == m.Project.Id);
                    var paths = m.Selection!.Paths.Select(path =>
                    {
                        var root = m.Project.Roots.OrderByDescending(r => r.Path.Length).FirstOrDefault(r => PathSafety.IsWithin(path, r.Path))
                            ?? throw new InvalidDataException("Recovered selection is outside its project roots.");
                        var current = project.Roots.SingleOrDefault(r => r.Id == root.Id)
                            ?? throw new InvalidDataException("Recovered selection's root identity no longer exists locally.");
                        return Path.Combine(current.Path, Path.GetRelativePath(root.Path, path));
                    }).ToList();
                    return m.Selection with { Paths = paths };
                }).ToList();
            // Validate everything before writing; preserve local projects, selections and jobs.
            foreach (var project in projects) WriteProject(db, transaction, project);
            foreach (var selection in recovered) Upsert(db, transaction, "selections", selection.Id, selection);
            var existingJobs = ReadAll<JobRecord>(db, "history", transaction).Select(j => j.Id).ToHashSet();
            foreach (var job in history.Where(j => !existingJobs.Contains(j.Id))) Upsert(db, transaction, "history", job.Id, job);
            transaction.Commit(); return projects.Count;
        }
    }
}
