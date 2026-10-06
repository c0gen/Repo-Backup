using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using RepoBackup.Core.Discovery;
using RepoBackup.Core.Infrastructure;

namespace RepoBackup.Tests;

internal static class ExtensionDiscoveryFixtures
{
    public static async Task<string> SharedHashAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    public static string Registry(string path, IReadOnlyDictionary<string, string> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open(); using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE ItemTable (key TEXT PRIMARY KEY,value TEXT NOT NULL)"; command.ExecuteNonQuery();
        foreach (var (key, value) in records)
        {
            command.CommandText = "INSERT INTO ItemTable(key,value) VALUES($key,$value)"; command.Parameters.Clear();
            command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$value", value); command.ExecuteNonQuery();
        }
        return path;
    }

    public static string RecentFolders(params string[] folders) => Json.Write(new
    { entries = folders.Select(folder => new { folderUri = DiscoveryFixtures.FileUri(folder) }).ToArray() });

    public static async Task<string> Workspace(string userData, string id, string reference, IReadOnlyDictionary<string, string>? extensions = null, bool workspace = false)
    {
        var storage = DiscoveryFixtures.Folder(userData, Path.Combine("User", "workspaceStorage", id));
        await File.WriteAllTextAsync(Path.Combine(storage, "workspace.json"), workspace
            ? Json.Write(new { workspace = DiscoveryFixtures.FileUri(reference) }) : Json.Write(new { folder = DiscoveryFixtures.FileUri(reference) }));
        if (extensions is not null) Registry(Path.Combine(storage, "state.vscdb"), extensions);
        return storage;
    }

    public static string CodexDatabase(string home, int version, (string Path, string Source)[] folders, DiscoveredProject[]? projects = null)
    {
        Directory.CreateDirectory(home); var path = Path.Combine(home, "state_" + version + ".sqlite");
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open(); using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE threads (cwd TEXT,source TEXT,title TEXT,first_user_message TEXT);" +
            "CREATE TABLE projects (id TEXT PRIMARY KEY,name TEXT,metadata TEXT);" +
            "CREATE TABLE project_roots (project_id TEXT,position INTEGER,path TEXT);";
        command.ExecuteNonQuery();
        foreach (var (folder, source) in folders)
        {
            command.CommandText = "INSERT INTO threads VALUES($cwd,$source,'fixture-private-data','fixture-private-data')"; command.Parameters.Clear();
            command.Parameters.AddWithValue("$cwd", folder); command.Parameters.AddWithValue("$source", source); command.ExecuteNonQuery();
        }
        foreach (var project in projects ?? [])
        {
            command.CommandText = "INSERT INTO projects VALUES($id,$name,'fixture-private-data')"; command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", project.ExternalId); command.Parameters.AddWithValue("$name", project.Name); command.ExecuteNonQuery();
            for (var i = 0; i < project.Roots.Count; i++)
            {
                command.CommandText = "INSERT INTO project_roots VALUES($id,$position,$path)"; command.Parameters.Clear();
                command.Parameters.AddWithValue("$id", project.ExternalId); command.Parameters.AddWithValue("$position", i);
                command.Parameters.AddWithValue("$path", project.Roots[i]); command.ExecuteNonQuery();
            }
        }
        return path;
    }

    public static string CopilotHeader(string cwd, string producer = "copilot-agent") => Json.Write(new
    { type = "session.start", data = new { producer, context = new { cwd }, sessionId = "fixture-private-session-id" } }).Replace("\r", "").Replace("\n", "");

    public static string TranscriptDirectory(string storage) => DiscoveryFixtures.Folder(storage, Path.Combine("GitHub.copilot-chat", "transcripts"));
}
