using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using RepoBackup.Core.Discovery;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using RepoBackup.Core.Storage;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class CatalogMigrationTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        await tests.Run("Version 1 database migration preserves identity, selections, exclusions and history", () =>
        {
            var directory = Path.Combine(root, "migration"); var paths = new AppPaths(Path.Combine(directory, "catalog"));
            var folder = DiscoveryFixtures.Folder(directory, "source"); var project = new ProjectEntry
            {
                Name = "Legacy project", Origin = "Codex", ExternalId = "legacy-id", Roots = [new("legacy-root", folder)], Reviewed = true, Enabled = true,
                Exclusions = new() { RelativePatterns = ["scratch/*"] }
            };
            var payload = LegacyPayload(project);
            var selection = new SavedSelection { ProjectId = project.Id, Name = "Legacy selection", Paths = [folder] };
            var job = new JobRecord { ProjectId = project.Id, ProjectName = project.Name, SeriesId = "project-" + project.Id, DestinationId = "fixture", FinishedAt = DateTimeOffset.UtcNow, Backup = Outcome.Successful, Verification = Outcome.Successful };
            CreateVersion1(paths.DatabasePath, project, payload, selection, job);
            var catalog = new CatalogStore(paths); var migrated = catalog.Projects().Single();
            Assert(migrated.Id == project.Id && migrated.Roots.Single().Id == "legacy-root" && migrated.Enabled && migrated.Exclusions.RelativePatterns.Single() == "scratch/*", "Migration reset project state.");
            Assert(migrated.DiscoverySources.Single() == new DiscoverySource(DiscoveryProviders.Codex, "legacy-id") && catalog.Selections().Single().Id == selection.Id && catalog.History().Single().Id == job.Id, "Migration lost metadata or history.");
            catalog.RegisterDiscovered("Independent", [Path.Combine(directory, "other")], DiscoveryProviders.ClaudeCode, "legacy-id");
            Assert(catalog.Projects().Count == 2 && Json.Read<CatalogExport>(catalog.Export()).SchemaVersion == 2, "Legacy global external-ID constraint remains.");
            var copy = DiscoveryFixtures.Catalog(Path.Combine(directory, "copy")); copy.Import(catalog.Export());
            Assert(copy.Projects().Count == 2 && copy.History().Single().Id == job.Id, "Version 2 export did not roundtrip."); return Task.CompletedTask;
        });

        await tests.Run("Legacy portable imports backfill discovery bindings without changing history", () =>
        {
            var directory = Path.Combine(root, "legacy-import"); var catalog = DiscoveryFixtures.Catalog(directory);
            var project = new ProjectEntry { Origin = "Codex", ExternalId = "old-id", Roots = [new("old-root", Path.Combine(directory, "source"))], Reviewed = true, Enabled = true };
            var document = JsonNode.Parse(Json.Write(new CatalogExport(1, [project], [], [], [], [])))!.AsObject();
            var legacy = document["projects"]![0]!.AsObject(); legacy.Remove("discoverySources"); legacy["roots"]![0]!.AsObject().Remove("aliases");
            catalog.Import(document.ToJsonString()); var imported = catalog.Projects().Single();
            Assert(imported.Id == project.Id && imported.Roots.Single().Id == "old-root" && imported.DiscoverySources.Single().ProviderId == DiscoveryProviders.Codex && imported.Enabled, "Legacy import lost identity or review."); return Task.CompletedTask;
        });

        await tests.Run("A failed version 1 migration rolls back both schema and payload changes", async () =>
        {
            var directory = Path.Combine(root, "failed-migration"); var paths = new AppPaths(Path.Combine(directory, "catalog"));
            var project = new ProjectEntry { Origin = "Codex", Roots = [new("invalid-root", "relative-invalid-path")] };
            var payload = LegacyPayload(project); CreateVersion1(paths.DatabasePath, project, payload, null, null);
            await Throws<ArgumentException>(() => { _ = new CatalogStore(paths); return Task.CompletedTask; });
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath }.ToString()); db.Open(); using var command = db.CreateCommand();
            command.CommandText = "PRAGMA user_version"; Assert(Convert.ToInt32(command.ExecuteScalar()) == 1, "Failed migration changed schema version.");
            command.CommandText = "SELECT payload FROM projects"; Assert((string)command.ExecuteScalar()! == payload, "Failed migration changed payload.");
        });
    }

    private static string LegacyPayload(ProjectEntry project)
    {
        var document = JsonNode.Parse(Json.Write(project))!.AsObject(); document.Remove("discoverySources");
        foreach (var root in document["roots"]!.AsArray()) root!.AsObject().Remove("aliases"); return document.ToJsonString();
    }

    private static void CreateVersion1(string database, ProjectEntry project, string payload, SavedSelection? selection, JobRecord? job)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()); db.Open();
        using var command = db.CreateCommand(); command.CommandText = """
            CREATE TABLE projects(id TEXT PRIMARY KEY,external_id TEXT UNIQUE,payload TEXT NOT NULL);
            CREATE TABLE project_roots(path TEXT COLLATE NOCASE PRIMARY KEY,project_id TEXT NOT NULL,root_id TEXT NOT NULL);
            CREATE TABLE selections(id TEXT PRIMARY KEY,payload TEXT NOT NULL);
            CREATE TABLE destinations(id TEXT PRIMARY KEY,payload TEXT NOT NULL);
            CREATE TABLE history(id TEXT PRIMARY KEY,payload TEXT NOT NULL);
            CREATE TABLE settings(id TEXT PRIMARY KEY,payload TEXT NOT NULL);
            PRAGMA user_version=1;
            """; command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO projects VALUES($id,$external,$payload)";
        command.Parameters.AddWithValue("$id", project.Id); command.Parameters.AddWithValue("$external", (object?)project.ExternalId ?? DBNull.Value); command.Parameters.AddWithValue("$payload", payload); command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO project_roots VALUES($path,$id,$root)"; command.Parameters.AddWithValue("$path", project.Roots.Single().Path); command.Parameters.AddWithValue("$root", project.Roots.Single().Id); command.ExecuteNonQuery();
        if (selection is not null)
        {
            command.CommandText = "INSERT INTO selections VALUES($selection,$selectionPayload)"; command.Parameters.AddWithValue("$selection", selection.Id); command.Parameters.AddWithValue("$selectionPayload", Json.Write(selection)); command.ExecuteNonQuery();
        }
        if (job is not null)
        {
            command.CommandText = "INSERT INTO history VALUES($job,$jobPayload)"; command.Parameters.AddWithValue("$job", job.Id); command.Parameters.AddWithValue("$jobPayload", Json.Write(job)); command.ExecuteNonQuery();
        }
    }
}
