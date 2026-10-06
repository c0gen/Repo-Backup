using System.Text.Json;
using RepoBackup.Core.Infrastructure;

namespace RepoBackup.Core.Discovery;

internal static class CopilotSessionReader
{
    public static async Task<DiscoveredProject?> ReadAsync(string path, CancellationToken token)
    {
        using var document = JsonDocument.Parse(await DiscoveryFiles.ReadFirstLineAsync(path, 1024 * 1024, token).ConfigureAwait(false));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.GetString() != "session.start") return null;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid Copilot session metadata.");
        if (!data.TryGetProperty("producer", out var producer) || producer.GetString() != "copilot-agent") return null;
        if (!data.TryGetProperty("context", out var context) || context.ValueKind != JsonValueKind.Object || !context.TryGetProperty("cwd", out var cwd)) return null;
        var folder = WorkspaceFileReader.LocalPath(cwd.GetString() ?? "");
        return folder is null ? null : new("folder:" + DiscoveryFiles.PathId(folder), PathSafety.DisplayName(folder), [folder])
        { AssociatedProviders = [DiscoveryProviders.Copilot] };
    }
}
