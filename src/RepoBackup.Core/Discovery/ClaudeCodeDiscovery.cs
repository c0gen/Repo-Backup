using System.Text.Json;
using RepoBackup.Core.Infrastructure;

namespace RepoBackup.Core.Discovery;

public sealed class ClaudeCodeDiscovery(string? configPath = null, string? projectsDirectory = null) : IProjectDiscoveryProvider
{
    public string Id => DiscoveryProviders.ClaudeCode;

    public static List<DiscoveredProject> ParseConfig(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid Claude Code config.");
        if (!document.RootElement.TryGetProperty("projects", out var projects)) return [];
        if (projects.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid Claude Code project registry.");
        return projects.EnumerateObject().Select(item => Folder(item.Name)).Where(p => p is not null).Cast<DiscoveredProject>().ToList();
    }

    public static List<DiscoveredProject> ParseSessionIndex(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid Claude Code session index.");
        var paths = new List<string>();
        if (root.TryGetProperty("originalPath", out var original)) paths.Add(original.GetString() ?? "");
        if (root.TryGetProperty("entries", out var entries))
        {
            if (entries.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid Claude Code session index entries.");
            foreach (var entry in entries.EnumerateArray())
                if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("projectPath", out var path)) paths.Add(path.GetString() ?? "");
        }
        return paths.Select(Folder).Where(p => p is not null).Cast<DiscoveredProject>().DistinctBy(p => p.ExternalId).ToList();
    }

    private static DiscoveredProject? Folder(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return null;
        path = PathSafety.Normalize(path);
        return new("folder:" + DiscoveryFiles.PathId(path), PathSafety.DisplayName(path), [path]);
    }

    public async Task<ProviderDiscoveryResult> ReadAsync(CancellationToken token = default)
    {
        var result = new List<DiscoveredProject>(); var warnings = new List<string>();
        var config = configPath ?? AppPaths.DefaultClaudeConfigPath;
        var indexes = projectsDirectory ?? (configPath is null || PathSafety.Normalize(configPath).Equals(PathSafety.Normalize(AppPaths.DefaultClaudeConfigPath), StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(AppPaths.DefaultClaudeHome, "projects") : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, "projects"));
        try { result.AddRange(ParseConfig(await DiscoveryFiles.ReadAsync(config, token).ConfigureAwait(false))); }
        catch (Exception e) when ((e is FileNotFoundException or DirectoryNotFoundException) && configPath is null) { }
        catch (Exception e) when (DiscoveryFiles.IsReadError(e)) { warnings.Add(DiscoveryFiles.Warning("Claude Code config", e)); }
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(indexes))
            {
                token.ThrowIfCancellationRequested(); var index = Path.Combine(directory, "sessions-index.json");
                try { result.AddRange(ParseSessionIndex(await DiscoveryFiles.ReadAsync(index, token).ConfigureAwait(false))); }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { }
                catch (Exception e) when (DiscoveryFiles.IsReadError(e)) { warnings.Add(DiscoveryFiles.Warning("Claude Code session index", e)); }
            }
        }
        catch (DirectoryNotFoundException) { }
        catch (Exception e) when (DiscoveryFiles.IsReadError(e)) { warnings.Add(DiscoveryFiles.Warning("Claude Code session indexes", e)); }
        return new(result.DistinctBy(p => p.ExternalId).ToList(), warnings.Distinct().ToList());
    }
}
