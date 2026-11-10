using Microsoft.Data.Sqlite;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Storage;

public sealed partial class CatalogStore
{
    public void ResolveDuplicateProjects(string survivingProjectId)
    {
        lock (writeGate)
        {
            using var db = Open(); using var transaction = db.BeginTransaction(deferred: false);
            var projects = ReadAll<ProjectEntry>(db, "projects", transaction);
            var survivor = projects.Single(p => p.Id == survivingProjectId);
            var key = CatalogIdentityReconciler.RootKey(survivor);
            var duplicates = projects.Where(p => p.Id != survivor.Id && CatalogIdentityReconciler.RootKey(p).Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (duplicates.Count == 0) throw new InvalidOperationException("No duplicate with the same complete source-root set is available to merge. Relink differing source roots instead.");
            foreach (var duplicate in duplicates) survivor = CatalogIdentityReconciler.Merge(survivor, duplicate, equivalent: true);
            var remaining = projects.Where(p => p.Id != survivor.Id && duplicates.All(d => d.Id != p.Id)).Append(survivor with { IdentityConflict = false });
            ReconcileIdentities(db, transaction, remaining, validateOwnership: true); transaction.Commit();
        }
    }

    public string CanonicalProjectId(string id) => Projects().FirstOrDefault(p => CatalogIdentityReconciler.Owns(p, id))?.Id ?? id;
    public string CanonicalSeries(string series)
    {
        return series.StartsWith("project-", StringComparison.Ordinal) ? "project-" + CanonicalProjectId(series[8..]) : series;
    }
    public HashSet<string> SeriesAliases(string series)
    {
        if (!series.StartsWith("project-", StringComparison.Ordinal)) return [series];
        var project = Projects().FirstOrDefault(p => CatalogIdentityReconciler.Owns(p, series[8..]));
        return project is null ? [series] : project.LegacyIds.Prepend(project.Id).Select(id => "project-" + id).ToHashSet(StringComparer.Ordinal);
    }

    private static JobRecord ResolveJob(JobRecord job, IReadOnlyList<ProjectEntry> projects)
    {
        var owner = projects.FirstOrDefault(p => CatalogIdentityReconciler.Owns(p, job.ProjectId));
        return owner is null || owner.Id == job.ProjectId ? job : job with { ProjectId = owner.Id,
            SeriesId = job.SeriesId == "project-" + job.ProjectId ? "project-" + owner.Id : job.SeriesId };
    }

    private static void ReconcileIdentities(SqliteConnection db, SqliteTransaction transaction, IEnumerable<ProjectEntry>? proposed = null, bool validateOwnership = false)
    {
        var history = ReadAll<JobRecord>(db, "history", transaction);
        var selections = ReadAll<SavedSelection>(db, "selections", transaction);
        var projects = CatalogIdentityReconciler.Reconcile(proposed ?? ReadAll<ProjectEntry>(db, "projects", transaction), history, selections);
        if (validateOwnership) foreach (var project in projects) ValidateOwnership(project, projects);
        using var clear = db.CreateCommand(); clear.Transaction = transaction;
        clear.CommandText = "DELETE FROM project_roots; DELETE FROM project_discovery_sources; DELETE FROM projects"; clear.ExecuteNonQuery();
        foreach (var project in projects) WriteProject(db, transaction, project);
        foreach (var selection in selections)
        {
            var owner = projects.FirstOrDefault(p => CatalogIdentityReconciler.Owns(p, selection.ProjectId));
            if (owner is not null) Upsert(db, transaction, "selections", selection.Id, selection with { ProjectId = owner.Id, Paths = selection.Paths.Select(PathSafety.Normalize).ToList() });
        }
    }

    private void BackupBeforeMigration(SqliteConnection source, int version)
    {
        if (version == 0 || version >= SchemaVersion) return;
        var filename = new SqliteConnectionStringBuilder(connectionString).DataSource;
        var backupPath = filename + ".pre-v4-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8] + ".db";
        using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Pooling = false }.ToString());
        backup.Open(); source.BackupDatabase(backup);
    }
}
