using Microsoft.Data.Sqlite;
using RepoBackup.Core.Infrastructure;

namespace RepoBackup.Core.Discovery;

internal static class CodexMetadataReader
{
    public static async Task<ProviderDiscoveryResult> ReadAsync(string home, CancellationToken token)
    {
        var projects = new List<DiscoveredProject>(); var warnings = new List<string>();
        try
        {
            token.ThrowIfCancellationRequested();
            var database = Directory.EnumerateFiles(home, "state_*.sqlite").Select(path =>
                (Path: path, Version: long.TryParse(Path.GetFileNameWithoutExtension(path).AsSpan(6), out var version) ? version : -1))
                .Where(item => item.Version >= 0).OrderByDescending(item => item.Version).FirstOrDefault().Path;
            if (database is null) return new([], []);
            await using var db = await DiscoverySqlite.OpenAsync(database, token).ConfigureAwait(false);
            using var command = db.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('projects','project_roots','threads')";
            var tables = new HashSet<string>();
            await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                while (await reader.ReadAsync(token).ConfigureAwait(false)) tables.Add(reader.GetString(0));
            if (tables.Contains("projects") && tables.Contains("project_roots"))
            {
                try { projects.AddRange(await ReadProjectsAsync(db, token).ConfigureAwait(false)); }
                catch (Exception e) when (DiscoveryFiles.IsReadError(e) || e is SqliteException) { warnings.Add(DiscoveryFiles.Warning("Codex saved project metadata", e)); }
            }
            if (tables.Contains("threads"))
            {
                try { projects.AddRange(await ReadFoldersAsync(db, token).ConfigureAwait(false)); }
                catch (Exception e) when (DiscoveryFiles.IsReadError(e) || e is SqliteException) { warnings.Add(DiscoveryFiles.Warning("Codex thread metadata", e)); }
            }
            if (!tables.Contains("threads") && !(tables.Contains("projects") && tables.Contains("project_roots")))
                throw new InvalidDataException("Unrecognized Codex metadata database.");
        }
        catch (DirectoryNotFoundException) { }
        catch (Exception e) when (DiscoveryFiles.IsReadError(e) || e is SqliteException) { warnings.Add(DiscoveryFiles.Warning("Codex metadata database", e)); }
        return new(projects, warnings.Distinct().ToList());
    }

    private static async Task<List<DiscoveredProject>> ReadProjectsAsync(SqliteConnection db, CancellationToken token)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT p.id,p.name,r.path FROM projects p JOIN project_roots r ON r.project_id=p.id ORDER BY p.id,r.position";
        var groups = new Dictionary<string, (string Name, List<string> Roots)>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            var root = reader.IsDBNull(2) ? null : WorkspaceFileReader.LocalPath(reader.GetString(2)); if (root is null) continue;
            var id = reader.GetString(0);
            if (!groups.TryGetValue(id, out var group)) groups[id] = group = (reader.IsDBNull(1) ? PathSafety.DisplayName(root) : reader.GetString(1), []);
            group.Roots.Add(root);
        }
        return groups.Select(g => new DiscoveredProject(g.Key, g.Value.Name, g.Value.Roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList())).ToList();
    }

    private static async Task<List<DiscoveredProject>> ReadFoldersAsync(SqliteConnection db, CancellationToken token)
    {
        using var command = db.CreateCommand();
        // No title, messages, instructions, transcript paths, or subagent records.
        command.CommandText = "SELECT DISTINCT cwd FROM threads WHERE source IN ('cli','vscode')";
        var result = new List<DiscoveredProject>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            var folder = reader.IsDBNull(0) ? null : WorkspaceFileReader.LocalPath(reader.GetString(0));
            if (folder is not null) result.Add(new("folder:" + DiscoveryFiles.PathId(folder), PathSafety.DisplayName(folder), [folder]));
        }
        return result.DistinctBy(p => p.ExternalId).ToList();
    }
}
