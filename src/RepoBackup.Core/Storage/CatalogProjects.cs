using Microsoft.Data.Sqlite;
using RepoBackup.Core.Discovery;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Storage;

public sealed record CandidateRegistrationResult(List<ProjectEntry> Projects, int Added);

public sealed partial class CatalogStore
{
    private static void WriteProject(SqliteConnection db, SqliteTransaction transaction, ProjectEntry project)
    {
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO projects(id,external_id,payload) VALUES($id,$external,$payload) ON CONFLICT(id) DO UPDATE SET external_id=excluded.external_id,payload=excluded.payload";
        command.Parameters.AddWithValue("$id", project.Id); command.Parameters.AddWithValue("$external", (object?)project.ExternalId ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload", Json.Write(project)); command.ExecuteNonQuery();
        command.CommandText = "DELETE FROM project_roots WHERE project_id=$id; DELETE FROM project_discovery_sources WHERE project_id=$id";
        command.ExecuteNonQuery();
        foreach (var root in project.Roots)
        {
            using var insert = db.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO project_roots(path,project_id,root_id) VALUES($path,$project,$root)";
            insert.Parameters.AddWithValue("$path", root.Path); insert.Parameters.AddWithValue("$project", project.Id);
            insert.Parameters.AddWithValue("$root", root.Id); insert.ExecuteNonQuery();
        }
        foreach (var source in project.DiscoverySources)
        {
            using var insert = db.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO project_discovery_sources(provider_id,external_id,project_id) VALUES($provider,$external,$project)";
            insert.Parameters.AddWithValue("$provider", source.ProviderId); insert.Parameters.AddWithValue("$external", source.ExternalId ?? "");
            insert.Parameters.AddWithValue("$project", project.Id); insert.ExecuteNonQuery();
        }
    }

    private static ProjectEntry PrepareSavedProject(ProjectEntry project, ProjectEntry? current)
    {
        project = ProjectIdentity.Normalize(project);
        if (current is null) return project;
        return project with
        {
            DiscoverySources = current.DiscoverySources.Concat(project.DiscoverySources).Distinct().ToList(),
            Roots = project.Roots.Select(root => current.Roots.FirstOrDefault(r => r.Id == root.Id) is { } old
                ? ProjectIdentity.Observe(root with { PreviousPaths = old.PreviousPaths.Concat(root.PreviousPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToList() }, old.Aliases)
                : root).ToList()
        };
    }

    private static void ValidateOwnership(ProjectEntry project, IEnumerable<ProjectEntry> otherProjects)
    {
        var roots = project.Roots.Select(r => (Root: r, Keys: ProjectIdentity.Keys(r))).ToList();
        if (roots.Select(r => r.Root.Id).Distinct().Count() != roots.Count) throw new InvalidDataException("Duplicate root identity.");
        for (var i = 0; i < roots.Count; i++)
        {
            if (roots.Skip(i + 1).Any(r => r.Keys.Overlaps(roots[i].Keys))) throw new InvalidDataException("Project repeats the same source folder.");
            if (otherProjects.Where(p => p.Id != project.Id).Any(p => p.Roots.Any(r => ProjectIdentity.Keys(r).Overlaps(roots[i].Keys))))
                throw new InvalidDataException("A source folder or its alias is already registered by another project.");
        }
    }

    public void SaveProject(ProjectEntry project)
    {
        lock (writeGate)
        {
            using var db = Open(); using var transaction = db.BeginTransaction(deferred: false);
            var existing = ReadAll<ProjectEntry>(db, "projects", transaction);
            project = PrepareSavedProject(project, existing.FirstOrDefault(p => p.Id == project.Id));
            ValidateOwnership(project, existing); WriteProject(db, transaction, project); transaction.Commit();
        }
    }

    public ProjectEntry RegisterCandidate(string name, IEnumerable<string> roots, string origin, string? externalId = null, bool approved = false) =>
        RegisterDiscovered(name, roots, DiscoveryProviders.FromOrigin(origin), externalId, approved, origin).Projects[0];

    public CandidateRegistrationResult RegisterDiscovered(string name, IEnumerable<string> roots, string providerId,
        string? externalId = null, bool approved = false, string? origin = null)
    {
        if (string.IsNullOrWhiteSpace(providerId)) throw new ArgumentException("Choose a discovery provider.");
        var paths = roots.Select(PathSafety.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count == 0) throw new ArgumentException("No source paths supplied.");
        var source = new DiscoverySource(providerId, string.IsNullOrWhiteSpace(externalId) ? null : externalId);
        lock (writeGate)
        {
            using var db = Open(); using var transaction = db.BeginTransaction(deferred: false);
            var existing = ReadAll<ProjectEntry>(db, "projects", transaction);
            var indexed = existing.SelectMany(p => p.Roots.Select(r => (Project: p, Root: r, Keys: ProjectIdentity.Keys(r)))).ToList();
            var affected = new Dictionary<string, ProjectEntry>(); var unmatched = new List<SourceRoot>();
            foreach (var path in paths)
            {
                var keys = ProjectIdentity.PathKeys(path);
                var matches = indexed.Where(r => r.Keys.Overlaps(keys)).ToList();
                if (matches.Count == 0)
                {
                    var duplicate = unmatched.FindIndex(r => ProjectIdentity.Keys(r).Overlaps(keys));
                    if (duplicate >= 0) unmatched[duplicate] = ProjectIdentity.Observe(unmatched[duplicate], [path]);
                    else unmatched.Add(ProjectIdentity.Observe(new(Guid.NewGuid().ToString("N"), path), []));
                    continue;
                }
                foreach (var match in matches)
                {
                    var project = affected.GetValueOrDefault(match.Project.Id) ?? match.Project;
                    project = project with
                    {
                        Roots = project.Roots.Select(r => r.Id == match.Root.Id ? ProjectIdentity.Observe(r, [path]) : r).ToList(),
                        DiscoverySources = project.DiscoverySources.Append(source).Distinct().ToList(),
                        ExternalId = project.ExternalId ?? externalId,
                        Reviewed = approved || project.Reviewed,
                        Enabled = approved || project.Enabled,
                        Dismissed = !approved && project.Dismissed
                    };
                    affected[project.Id] = project;
                }
            }
            foreach (var project in affected.Values)
                if (Json.Write(project) != Json.Write(existing.Single(p => p.Id == project.Id))) WriteProject(db, transaction, project);
            if (unmatched.Count > 0)
            {
                var created = new ProjectEntry
                {
                    Name = string.IsNullOrWhiteSpace(name) ? PathSafety.DisplayName(unmatched[0].Path) : name,
                    Roots = unmatched, Origin = origin ?? DiscoveryProviders.DisplayName(providerId), ExternalId = externalId,
                    DiscoverySources = [source], Reviewed = approved, Enabled = approved
                };
                ValidateOwnership(created, existing); WriteProject(db, transaction, created); affected[created.Id] = created;
            }
            transaction.Commit(); return new(affected.Values.ToList(), unmatched.Count > 0 ? 1 : 0);
        }
    }

    public void RelinkRoot(string projectId, string rootId, string newPath)
    {
        newPath = PathSafety.Normalize(newPath);
        lock (writeGate)
        {
            using var db = Open(); using var transaction = db.BeginTransaction(deferred: false);
            var existing = ReadAll<ProjectEntry>(db, "projects", transaction);
            var project = existing.Single(p => p.Id == projectId); var oldRoot = project.Roots.Single(r => r.Id == rootId);
            project = ProjectIdentity.Normalize(project with
            {
                Roots = project.Roots.Select(r => r.Id == rootId
                    ? r with { Path = newPath, PreviousPaths = r.PreviousPaths.Append(r.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), Aliases = ProjectIdentity.Keys(r).ToList() }
                    : r).ToList()
            });
            ValidateOwnership(project, existing); WriteProject(db, transaction, project);
            foreach (var selection in ReadAll<SavedSelection>(db, "selections", transaction).Where(s => s.ProjectId == projectId))
            {
                var aliases = ProjectIdentity.Keys(oldRoot);
                var updated = selection with { Paths = selection.Paths.Select(path =>
                {
                    var parent = aliases.OrderByDescending(p => p.Length).FirstOrDefault(p => PathSafety.IsWithin(path, p));
                    return parent is null ? path : Path.Combine(newPath, Path.GetRelativePath(parent, path));
                }).ToList() };
                Upsert(db, transaction, "selections", updated.Id, updated);
            }
            transaction.Commit();
        }
    }
}
