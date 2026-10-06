using System.Text;
using Microsoft.Data.Sqlite;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Storage;

namespace RepoBackup.Tests;

internal static class DiscoveryFixtures
{
    public static CatalogStore Catalog(string directory) => new(new AppPaths(Path.Combine(directory, "catalog")));

    public static string Folder(string directory, string name)
    {
        var path = Path.Combine(directory, name); Directory.CreateDirectory(path); return path;
    }

    public static async Task<string> CodexState(string directory, params string[] roots)
    {
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "codex.json");
        await File.WriteAllTextAsync(path, Json.Write(new Dictionary<string, object>
        { ["local-projects"] = new Dictionary<string, object> { ["shared-id"] = new { name = "Shared project", rootPaths = roots } } }));
        return path;
    }

    public static async Task<string> ClaudeState(string directory, params string[] roots)
    {
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "claude.json");
        await File.WriteAllTextAsync(path, Json.Write(new
        {
            projects = roots.ToDictionary(p => p, _ => new { ignoredPrivateSetting = "fixture-private-data" }),
            oauthAccount = new { token = "fixture-private-data" }
        }));
        return path;
    }

    public static string AntigravityDatabase(string directory, IReadOnlyDictionary<string, string> records)
    {
        var userData = Path.Combine(directory, "antigravity"); var storage = Folder(userData, "User\\globalStorage");
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(storage, "state.vscdb"), Pooling = false }.ToString());
        db.Open(); using var command = db.CreateCommand(); command.CommandText = "CREATE TABLE ItemTable (key TEXT PRIMARY KEY,value TEXT NOT NULL)"; command.ExecuteNonQuery();
        foreach (var (key, value) in records)
        {
            command.CommandText = "INSERT INTO ItemTable(key,value) VALUES($key,$value)"; command.Parameters.Clear();
            command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$value", value); command.ExecuteNonQuery();
        }
        return userData;
    }

    public static string FileUri(string path) => new Uri(PathSafety.Normalize(path)).AbsoluteUri;

    // Build realistic map entries, including an unrelated opaque row and forward-compatible fields.
    public static string Sidebar(params string[] uris)
    {
        var state = new List<byte>();
        foreach (var uri in uris)
        {
            var entry = new List<byte>(); Field(entry, 1, Encoding.UTF8.GetBytes(uri));
            var row = new List<byte>(); Field(row, 1, Encoding.UTF8.GetBytes("opaque-workspace-metadata")); Field(entry, 2, row.ToArray());
            Field(state, 1, entry.ToArray());
        }
        Field(state, 7, Encoding.UTF8.GetBytes("unknown-field")); return Convert.ToBase64String(state.ToArray());
    }

    private static void Field(List<byte> destination, uint number, byte[] bytes)
    { Varint(destination, number * 8 + 2); Varint(destination, (uint)bytes.Length); destination.AddRange(bytes); }
    private static void Varint(List<byte> destination, uint value)
    {
        while (value > 127) { destination.Add((byte)((value & 127) | 128)); value >>= 7; }
        destination.Add((byte)value);
    }
}
