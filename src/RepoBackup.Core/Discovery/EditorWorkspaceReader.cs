using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace RepoBackup.Core.Discovery;

internal sealed record EditorWorkspace(string StorageDirectory, DiscoveredProject? Project);

internal sealed class EditorWorkspaceReader(string source)
{
    public const string HistoryKey = "history.recentlyOpenedPathsList";
    public List<DiscoveredProject> Projects { get; } = [];
    public List<string> Warnings { get; } = [];

    public static string UserDirectory(string directory) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)).Equals("User", StringComparison.OrdinalIgnoreCase)
            ? directory : Path.Combine(directory, "User");

    public ProviderDiscoveryResult Result() => new(Projects.GroupBy(p => p.ExternalId).Select(group => group.First() with
    { AssociatedProviders = group.SelectMany(p => p.AssociatedProviders).Distinct().ToList() }).ToList(), Warnings.Distinct().ToList());

    public void Warn(string component, Exception error) => Warnings.Add(DiscoveryFiles.Warning(source + " " + component, error));

    public async Task<DiscoveredProject?> AddReferenceAsync(string reference, bool workspace, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var project = await WorkspaceFileReader.ReadReferenceAsync(reference, workspace, token).ConfigureAwait(false);
            if (project is not null) Projects.Add(project);
            return project;
        }
        // Stale workspace-file references are common. Offline volumes/shares must warn.
        catch (Exception e) when (DiscoveryFiles.IsMissingOnAvailableVolume(e, reference)) { return null; }
        catch (Exception e) when (DiscoveryFiles.IsReadError(e)) { Warn("referenced workspace", e); return null; }
    }

    public async Task ReadRegistryAsync(string database, IReadOnlyList<string>? extraKeys, Func<string, string, Task>? readExtra, CancellationToken token)
    {
        try
        {
            var keys = new List<string> { HistoryKey }; if (extraKeys is not null) keys.AddRange(extraKeys);
            foreach (var record in await DiscoverySqlite.ReadItemsAsync(database, keys, token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (record.Key == HistoryKey) await ReadHistoryAsync(record.Value, token).ConfigureAwait(false);
                    else if (readExtra is not null) await readExtra(record.Key, record.Value).ConfigureAwait(false);
                }
                catch (Exception e) when (DiscoveryFiles.IsReadError(e)) { Warn("workspace registry", e); }
            }
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception e) when (DiscoveryFiles.IsReadError(e) || e is SqliteException) { Warn("database", e); }
    }

    public async Task<List<EditorWorkspace>> ReadStorageAsync(string storage, CancellationToken token)
    {
        var result = new List<EditorWorkspace>();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(storage))
            {
                token.ThrowIfCancellationRequested(); DiscoveredProject? project = null;
                try
                {
                    using var metadata = JsonDocument.Parse(await DiscoveryFiles.ReadAsync(Path.Combine(directory, "workspace.json"), token).ConfigureAwait(false));
                    var root = metadata.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid workspace metadata.");
                    if (root.TryGetProperty("folder", out var folder)) project = await AddReferenceAsync(folder.GetString() ?? "", false, token).ConfigureAwait(false);
                    else if (root.TryGetProperty("workspace", out var workspace)) project = await AddReferenceAsync(workspace.GetString() ?? "", true, token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { }
                catch (Exception e) when (DiscoveryFiles.IsReadError(e)) { Warn("workspace metadata", e); }
                result.Add(new(directory, project));
            }
        }
        catch (DirectoryNotFoundException) { }
        catch (Exception e) when (DiscoveryFiles.IsReadError(e)) { Warn("workspace storage", e); }
        return result;
    }

    private async Task ReadHistoryAsync(string json, CancellationToken token)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Invalid recent-workspace history.");
        var references = new List<(string Uri, bool Workspace)>();
        // Validate the registry before emitting any of its candidates.
        foreach (var entry in entries.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            if (entry.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid recent-workspace entry.");
            if (entry.TryGetProperty("folderUri", out var folder)) references.Add((folder.GetString() ?? "", false));
            else if (entry.TryGetProperty("workspace", out var workspace) && workspace.TryGetProperty("configPath", out var config))
                references.Add((config.GetString() ?? "", true));
        }
        foreach (var reference in references) await AddReferenceAsync(reference.Uri, reference.Workspace, token).ConfigureAwait(false);
    }
}
