using Microsoft.Data.Sqlite;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Storage;

public sealed partial class CatalogStore
{
    public const int SchemaVersion = 2;

    private void InitializeSchema()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL"; command.ExecuteNonQuery();
        // Immediate transactions serialize schema changes and registration across processes.
        using var transaction = db.BeginTransaction(deferred: false); command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(command.ExecuteScalar());
        if (version is < 0 or > SchemaVersion) throw new InvalidDataException("Unsupported catalog schema. Existing catalog has been preserved.");
        if (version == SchemaVersion) { transaction.Commit(); return; }
        if (version == 1)
        {
            command.CommandText = "ALTER TABLE projects RENAME TO projects_v1"; command.ExecuteNonQuery();
        }
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS projects (id TEXT PRIMARY KEY, external_id TEXT, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS project_roots (path TEXT COLLATE NOCASE PRIMARY KEY, project_id TEXT NOT NULL, root_id TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS project_discovery_sources (
                provider_id TEXT NOT NULL, external_id TEXT NOT NULL, project_id TEXT NOT NULL,
                PRIMARY KEY(provider_id, external_id, project_id));
            CREATE TABLE IF NOT EXISTS selections (id TEXT PRIMARY KEY, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS destinations (id TEXT PRIMARY KEY, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS history (id TEXT PRIMARY KEY, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS settings (id TEXT PRIMARY KEY, payload TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
        if (version == 1)
        {
            command.CommandText = "INSERT INTO projects SELECT id,external_id,payload FROM projects_v1; DROP TABLE projects_v1;";
            command.ExecuteNonQuery();
            foreach (var project in ReadAll<ProjectEntry>(db, "projects", transaction))
                WriteProject(db, transaction, ProjectIdentity.Normalize(project));
        }
        command.CommandText = "PRAGMA user_version=2"; command.ExecuteNonQuery(); transaction.Commit();
    }
}
