using System.Text.Json;
using RepoBackup.Core.Infrastructure;

namespace RepoBackup.Core.Discovery;

public sealed class CodexDiscovery(string? statePath = null) : IProjectDiscoveryProvider
{
    public string Id => DiscoveryProviders.Codex;

    public static List<DiscoveredProject> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement; var result = new List<DiscoveredProject>();
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid Codex project registry.");
        if (root.TryGetProperty("local-projects", out var projects) && projects.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in projects.EnumerateObject())
            {
                var value = item.Value;
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("rootPaths", out var paths) || paths.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Codex project structure changed.");
                var roots = paths.EnumerateArray().Select(p => PathSafety.Normalize(p.GetString() ?? "")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (roots.Count == 0) continue;
                var name = value.TryGetProperty("name", out var n) ? n.GetString() : null;
                result.Add(new(item.Name, name ?? PathSafety.DisplayName(roots[0]), roots));
            }
        }
        else if (root.TryGetProperty("electron-saved-workspace-roots", out var legacy) && legacy.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in legacy.EnumerateArray())
            {
                var path = PathSafety.Normalize(value.GetString() ?? "");
                result.Add(new("legacy:" + DiscoveryFiles.PathId(path), PathSafety.DisplayName(path), [path]));
            }
        }
        else throw new InvalidDataException("Unrecognized Codex saved-project format.");
        return result;
    }

    public async Task<ProviderDiscoveryResult> ReadAsync(CancellationToken token = default)
    {
        var path = statePath ?? Path.Combine(AppPaths.DefaultCodexHome, ".codex-global-state.json");
        try { return new(Parse(await DiscoveryFiles.ReadAsync(path, token).ConfigureAwait(false)), []); }
        catch (Exception e) when ((e is FileNotFoundException or DirectoryNotFoundException) && statePath is null) { return new([], []); }
        catch (Exception e) when (DiscoveryFiles.IsReadError(e)) { return new([], [DiscoveryFiles.Warning("Codex", e)]); }
    }
}
