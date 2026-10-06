using System.Text.Json;
using Microsoft.Data.Sqlite;
using RepoBackup.Core.Infrastructure;

namespace RepoBackup.Core.Discovery;

public sealed class AntigravityDiscovery(string? userDataDirectory = null) : IProjectDiscoveryProvider
{
    public string Id => DiscoveryProviders.Antigravity;
    private const string HistoryKey = "history.recentlyOpenedPathsList";
    private const string SidebarKey = "antigravityUnifiedStateSync.sidebarWorkspaces";

    public async Task<ProviderDiscoveryResult> ReadAsync(CancellationToken token = default)
    {
        var result = new List<DiscoveredProject>(); var warnings = new List<string>();
        var directory = userDataDirectory ?? AppPaths.DefaultAntigravityUserDataDirectory;
        var userDirectory = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)).Equals("User", StringComparison.OrdinalIgnoreCase)
            ? directory : Path.Combine(directory, "User");
        var database = Path.Combine(userDirectory, "globalStorage", "state.vscdb");
        // Read only the known workspace keys. Never create or migrate another program's database.
        try
        {
            // File.Exists hides access-denied errors. Probe attributes so inaccessible
            // registries warn, while an absent installation still skips silently.
            File.GetAttributes(database);
            await using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 2 }.ToString());
            await db.OpenAsync(token).ConfigureAwait(false);
            using var command = db.CreateCommand();
            command.CommandText = "SELECT key,value FROM ItemTable WHERE key IN ($history,$sidebar,$legacy)";
            command.Parameters.AddWithValue("$history", HistoryKey); command.Parameters.AddWithValue("$sidebar", SidebarKey);
            command.Parameters.AddWithValue("$legacy", "google.antigravity");
            var records = new List<(string Key, string Value)>();
            await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                while (await reader.ReadAsync(token).ConfigureAwait(false)) records.Add((reader.GetString(0), reader.GetString(1)));
            foreach (var record in records)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (record.Key == SidebarKey)
                        foreach (var uri in AntigravitySidebarReader.ReadWorkspaceUris(record.Value)) await AddReference(uri, false);
                    else if (record.Key == HistoryKey) await ReadHistory(record.Value);
                    else await ReadLegacy(record.Value);
                }
                catch (Exception e) when (DiscoveryFiles.IsReadError(e)) { warnings.Add(DiscoveryFiles.Warning("Antigravity workspace registry", e)); }
            }
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception e) when (DiscoveryFiles.IsReadError(e) || e is SqliteException) { warnings.Add(DiscoveryFiles.Warning("Antigravity database", e)); }
        var storage = Path.Combine(userDirectory, "workspaceStorage");
        try
        {
            foreach (var workspaceDirectory in Directory.EnumerateDirectories(storage))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var metadata = JsonDocument.Parse(await DiscoveryFiles.ReadAsync(Path.Combine(workspaceDirectory, "workspace.json"), token).ConfigureAwait(false));
                    var root = metadata.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid workspace metadata.");
                    if (root.TryGetProperty("folder", out var folder)) await AddReference(folder.GetString() ?? "", false);
                    else if (root.TryGetProperty("workspace", out var workspace)) await AddReference(workspace.GetString() ?? "", true);
                }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { }
                catch (Exception e) when (DiscoveryFiles.IsReadError(e)) { warnings.Add(DiscoveryFiles.Warning("Antigravity workspace metadata", e)); }
            }
        }
        catch (DirectoryNotFoundException) { }
        catch (Exception e) when (DiscoveryFiles.IsReadError(e)) { warnings.Add(DiscoveryFiles.Warning("Antigravity workspace storage", e)); }
        return new(result.DistinctBy(p => p.ExternalId).ToList(), warnings.Distinct().ToList());

        async Task AddReference(string reference, bool workspace)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (await WorkspaceFileReader.ReadReferenceAsync(reference, workspace, token).ConfigureAwait(false) is { } project) result.Add(project);
            }
            catch (Exception e) when (DiscoveryFiles.IsReadError(e)) { warnings.Add(DiscoveryFiles.Warning("Antigravity referenced workspace", e)); }
        }

        async Task ReadHistory(string json)
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Invalid Antigravity recent-workspace history.");
            // Validate all references before emitting candidates from this registry value.
            var references = new List<(string Uri, bool Workspace)>();
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid recent-workspace entry.");
                if (entry.TryGetProperty("folderUri", out var folder)) references.Add((folder.GetString() ?? "", false));
                else if (entry.TryGetProperty("workspace", out var workspace) && workspace.TryGetProperty("configPath", out var config))
                    references.Add((config.GetString() ?? "", true));
            }
            foreach (var reference in references) await AddReference(reference.Uri, reference.Workspace);
        }

        async Task ReadLegacy(string json)
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid Antigravity workspace map.");
            if (!document.RootElement.TryGetProperty("antigravity.workspaceCascadeMap", out var map)) return;
            if (map.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid Antigravity workspace map.");
            foreach (var workspace in map.EnumerateObject()) await AddReference(workspace.Name, false);
        }
    }
}
