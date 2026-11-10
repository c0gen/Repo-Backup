using Microsoft.Data.Sqlite;
using RepoBackup.Core.Backup;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using RepoBackup.Core.Storage;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class CatalogIdentityRegressionTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        await tests.Run("Extended drive, UNC and long paths share canonical identity and containment", async () =>
        {
            Assert(PathSafety.Normalize(@"\\?\C:\project") == @"C:\project" && PathSafety.Normalize(@"\\?\UNC\server\share\project") == @"\\server\share\project", "Extended prefixes were not canonicalized.");
            Assert(PathSafety.IsWithin(@"\\?\C:\project\child", @"C:\project") && PathSafety.SnapshotPath(@"\\?\C:\project") == "/C/project", "Extended containment or snapshot path differs.");
            var longPath = @"C:\" + string.Join('\\', Enumerable.Repeat(new string('x', 100), 4));
            Assert(PathSafety.Normalize(@"\\?\" + longPath) == longPath, "Canonicalization truncated a long path.");
            await Throws<ArgumentException>(() => { PathSafety.Normalize(@"\\?\C:relative"); return Task.CompletedTask; });
            var catalog = new CatalogStore(new AppPaths(Path.Combine(root, "path-catalog")));
            var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
            var normal = catalog.RegisterCandidate("Normal", [source], "Manual", approved: true);
            var extended = catalog.RegisterCandidate("Extended", [@"\\?\" + source], "Fixture");
            Assert(normal.Id == extended.Id && catalog.Projects().Count == 1 && extended.Enabled, "Extended discovery duplicated or reset approval.");
        });
        await tests.Run("Version 3 duplicates migrate with backup, legacy IDs, selections and history", () =>
        {
            var paths = new AppPaths(Path.Combine(root, "migration")); var original = new CatalogStore(paths);
            var source = Path.Combine(root, "migration-source"); Directory.CreateDirectory(source);
            var first = original.RegisterCandidate("First", [source], "Manual", approved: true);
            var duplicate = first with { Id = Guid.NewGuid().ToString("N"), Name = "Verified duplicate", Roots = [new("duplicate-root", @"\\?\" + source)] };
            var selected = SavedSelection.FromProject(first, "Selection", [source]); original.SaveSelection(selected);
            var oldJob = new JobRecord { ProjectId = first.Id, ProjectName = first.Name, SeriesId = "project-" + first.Id, DestinationId = "fixture", Backup = Outcome.Cancelled, FinishedAt = DateTimeOffset.UtcNow };
            var verified = oldJob with { Id = Guid.NewGuid().ToString("N"), ProjectId = duplicate.Id, SeriesId = "project-" + duplicate.Id,
                Backup = Outcome.Successful, Verification = Outcome.Successful, SnapshotId = "verified" };
            original.SaveJob(oldJob); original.SaveJob(verified); InsertLegacyDuplicate(paths, duplicate);
            var migrated = new CatalogStore(paths); var owner = migrated.Projects().Single();
            Assert(owner.Id == duplicate.Id && owner.LegacyIds.Contains(first.Id) && owner.Roots.Single().LegacyIds.Contains(first.Roots.Single().Id)
                && owner.Roots.Single().Path == source, "Migration chose the wrong identity or lost legacy IDs.");
            Assert(migrated.History().Single(j => j.Id == oldJob.Id).ProjectId == owner.Id && migrated.Selections().Single().ProjectId == owner.Id, "History/selection association was lost.");
            using (var db = Open(paths.DatabasePath))
            {
                using var command = db.CreateCommand(); command.CommandText = "SELECT payload FROM history WHERE id=$id"; command.Parameters.AddWithValue("$id", oldJob.Id);
                Assert(Json.Read<JobRecord>((string)command.ExecuteScalar()!).ProjectId == first.Id, "Historical job payload was rewritten.");
            }
            var backup = Directory.GetFiles(paths.DataDirectory, "catalog.db.pre-v4-*.db").Single();
            using (var db = Open(backup))
            {
                using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version"; Assert(Convert.ToInt32(command.ExecuteScalar()) == 3, "Migration backup is not the original schema.");
                command.CommandText = "SELECT COUNT(*) FROM projects"; Assert(Convert.ToInt32(command.ExecuteScalar()) == 2, "Migration backup lost WAL data.");
            }
            var aliases = migrated.SeriesAliases("project-" + owner.Id);
            Assert(aliases.SetEquals(["project-" + first.Id, "project-" + duplicate.Id]), "Equivalent snapshot series were separated.");
            var now = DateTimeOffset.UtcNow; var last = now.AddDays(-1);
            var parent = new SnapshotSummary("parent", now.AddMinutes(-1), ["successful", "coverage=full", "series=project-" + first.Id, "full-read-at=" + last.UtcTicks]) { Hostname = "fixture" };
            Assert(BackupReadPolicy.Select([parent], "project-" + owner.Id, Coverage.FullProject, "fixture", BackupReadMode.Incremental, now, aliases).ParentId == "parent", "Legacy parent cannot be reused.");
            var copy = new CatalogStore(new AppPaths(Path.Combine(root, "roundtrip"))); copy.Import(migrated.Export());
            Assert(copy.Projects().Single().LegacyIds.Contains(first.Id) && copy.SeriesAliases("project-" + owner.Id).SetEquals(aliases), "Portable export lost identity aliases.");
            return Task.CompletedTask;
        });
        await tests.Run("Catalog rebuilding connects legacy snapshots across moved roots and preserves local settings", () =>
        {
            var old = new ProjectEntry { Id = "old", Roots = [new("old-root", Path.Combine(root, "previous-location"))] };
            var current = old with { Id = "current", LegacyIds = [old.Id], Roots = [new("current-root", Path.Combine(root, "current-location")) { LegacyIds = ["old-root"] }] };
            RecoveryManifest Manifest(ProjectEntry p) => new() { JobId = Guid.NewGuid().ToString("N"), SeriesId = "project-" + p.Id, Project = p };
            var manifests = new[] { Manifest(old), Manifest(current) };
            var catalog = new CatalogStore(new AppPaths(Path.Combine(root, "rebuilt")));
            catalog.RecoverCatalog(manifests, []);
            Assert(catalog.Projects().Single().Id == current.Id && catalog.CanonicalProjectId(old.Id) == current.Id,
                "Rebuilding duplicated a predecessor with a previous source location.");
            var local = new CatalogStore(new AppPaths(Path.Combine(root, "recovered-local")));
            local.SaveProject(old with { Reviewed = true, Enabled = true, Exclusions = new() { RelativePatterns = ["local/*"] } });
            local.RecoverCatalog(manifests, []);
            Assert(local.Projects().Single().Id == old.Id && local.CanonicalProjectId(current.Id) == old.Id
                && local.Projects().Single().Exclusions.RelativePatterns.Contains("local/*"), "Recovery replaced local project settings or lost incoming aliases.");
            return Task.CompletedTask;
        });
        await tests.Run("Conflicting duplicate rules survive migration as explicit coverage conflicts", () =>
        {
            var paths = new AppPaths(Path.Combine(root, "conflicting")); var catalog = new CatalogStore(paths);
            var source = Path.Combine(root, "conflicting-source");
            var first = catalog.RegisterCandidate("First", [source], "Manual", approved: true);
            InsertLegacyDuplicate(paths, first with { Id = "conflicting", Roots = [new("conflicting-root", @"\\?\" + source)], Exclusions = new() { RelativePatterns = ["private/*"] } });
            catalog = new CatalogStore(paths);
            Assert(catalog.Projects().Count == 2 && catalog.Projects().All(p => p.IdentityConflict), "Conflicting configurations were silently merged.");
            Assert(catalog.Projects().Any(p => p.Exclusions.RelativePatterns.Contains("private/*")), "Conflicting exclusion was lost.");
            catalog.ResolveDuplicateProjects(first.Id);
            Assert(catalog.Projects().Count == 1 && !catalog.Projects().Single().IdentityConflict && catalog.Projects().Single().Exclusions.RelativePatterns.Count == 0
                && catalog.Projects().Single().LegacyIds.Contains("conflicting"), "Explicit resolution lost the chosen rules or duplicate identity.");
            return Task.CompletedTask;
        });
        await tests.Run("Unused subset discoveries merge without claiming full multi-root history", () =>
        {
            var paths = new AppPaths(Path.Combine(root, "subset")); var catalog = new CatalogStore(paths);
            var source = Path.Combine(root, "models"); var other = Path.Combine(root, "other-models");
            var project = catalog.RegisterCandidate("Models", [source, other], "Manual", approved: true);
            var subset = new ProjectEntry { Id = "subset", Name = "Duplicate discovery", Roots = [new("subset-root", @"\\?\" + source)] };
            InsertLegacyDuplicate(paths, subset); catalog = new CatalogStore(paths);
            var owner = catalog.Projects().Single();
            Assert(owner.Id == project.Id && owner.Roots.Count == 2 && owner.Roots.Single(r => r.Path == source).LegacyIds.Contains("subset-root")
                && !owner.LegacyIds.Contains("subset"), "Subset merge lost roots or equated different full-project scopes.");
            return Task.CompletedTask;
        });
        await tests.Run("Failed version 3 identity migration rolls back schema and every payload", async () =>
        {
            var paths = new AppPaths(Path.Combine(root, "rollback")); var catalog = new CatalogStore(paths);
            var project = catalog.RegisterCandidate("Rollback", [Path.Combine(root, "rollback-source")], "Manual", approved: true);
            InsertLegacyDuplicate(paths, project with { Id = "rollback-duplicate", Roots = [new("rollback-root", @"\\?\" + project.Roots[0].Path)] });
            using (var db = Open(paths.DatabasePath))
            {
                using var command = db.CreateCommand(); command.CommandText = "CREATE TRIGGER reject_identity BEFORE INSERT ON projects BEGIN SELECT RAISE(ABORT,'injected migration failure'); END"; command.ExecuteNonQuery();
            }
            await Throws<SqliteException>(() => { _ = new CatalogStore(paths); return Task.CompletedTask; });
            using (var db = Open(paths.DatabasePath))
            {
                using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version"; Assert(Convert.ToInt32(command.ExecuteScalar()) == 3, "Failed migration changed the schema version.");
                command.CommandText = "SELECT COUNT(*) FROM projects"; Assert(Convert.ToInt32(command.ExecuteScalar()) == 2, "Failed migration removed projects.");
            }
        });
    }

    private static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); db.Open(); return db;
    }
    private static void InsertLegacyDuplicate(AppPaths paths, ProjectEntry project)
    {
        using var db = Open(paths.DatabasePath); using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO projects(id,external_id,payload) VALUES($id,NULL,$payload); PRAGMA user_version=3";
        command.Parameters.AddWithValue("$id", project.Id); command.Parameters.AddWithValue("$payload", Json.Write(project)); command.ExecuteNonQuery();
    }
}
