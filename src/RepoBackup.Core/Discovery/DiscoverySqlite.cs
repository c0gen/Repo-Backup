using Microsoft.Data.Sqlite;

namespace RepoBackup.Core.Discovery;

internal static class DiscoverySqlite
{
    public static async Task<SqliteConnection> OpenAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Probe attributes rather than hiding access failures behind File.Exists.
        File.GetAttributes(path);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 2 }.ToString());
        try { await db.OpenAsync(token).ConfigureAwait(false); return db; }
        catch { await db.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public static async Task<List<(string Key, string Value)>> ReadItemsAsync(string path, IReadOnlyList<string> keys, CancellationToken token)
    {
        await using var db = await OpenAsync(path, token).ConfigureAwait(false);
        using var command = db.CreateCommand();
        command.CommandText = "SELECT key,value FROM ItemTable WHERE key IN (" + Parameters(command, keys) + ")";
        var records = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            var value = reader.GetString(1);
            if (value.Length > 32 * 1024 * 1024) throw new InvalidDataException("Discovery metadata exceeds the supported size.");
            records.Add((reader.GetString(0), value));
        }
        return records;
    }

    public static async Task<List<string>> ReadKeysAsync(string path, IReadOnlyList<string> keys, CancellationToken token)
    {
        await using var db = await OpenAsync(path, token).ConfigureAwait(false);
        using var command = db.CreateCommand();
        // Extension state may contain conversations or credentials. Select its
        // ownership key to identify an association, never its private value.
        command.CommandText = "SELECT key FROM ItemTable WHERE lower(key) IN (" + Parameters(command, keys.Select(k => k.ToLowerInvariant()).ToList()) + ")";
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false)) { token.ThrowIfCancellationRequested(); result.Add(reader.GetString(0)); }
        return result;
    }

    private static string Parameters(SqliteCommand command, IReadOnlyList<string> values)
    {
        var names = new List<string>();
        for (var i = 0; i < values.Count; i++)
        { var name = "$key" + i; names.Add(name); command.Parameters.AddWithValue(name, values[i]); }
        return string.Join(",", names);
    }
}
