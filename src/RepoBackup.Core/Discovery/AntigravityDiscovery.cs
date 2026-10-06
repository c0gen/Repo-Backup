using System.Text.Json;
using RepoBackup.Core.Infrastructure;

namespace RepoBackup.Core.Discovery;

public sealed class AntigravityDiscovery(string? userDataDirectory = null) : IProjectDiscoveryProvider
{
    public string Id => DiscoveryProviders.Antigravity;
    private const string SidebarKey = "antigravityUnifiedStateSync.sidebarWorkspaces";
    private const string LegacyKey = "google.antigravity";

    public async Task<ProviderDiscoveryResult> ReadAsync(CancellationToken token = default)
    {
        var reader = new EditorWorkspaceReader("Antigravity");
        var userDirectory = EditorWorkspaceReader.UserDirectory(userDataDirectory ?? AppPaths.DefaultAntigravityUserDataDirectory);
        await reader.ReadRegistryAsync(Path.Combine(userDirectory, "globalStorage", "state.vscdb"), [SidebarKey, LegacyKey], ReadCustom, token).ConfigureAwait(false);
        await reader.ReadStorageAsync(Path.Combine(userDirectory, "workspaceStorage"), token).ConfigureAwait(false);
        return reader.Result();

        async Task ReadCustom(string key, string json)
        {
            if (key == SidebarKey)
            {
                foreach (var uri in AntigravitySidebarReader.ReadWorkspaceUris(json)) await reader.AddReferenceAsync(uri, false, token).ConfigureAwait(false);
                return;
            }
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid Antigravity workspace map.");
            if (!document.RootElement.TryGetProperty("antigravity.workspaceCascadeMap", out var map)) return;
            if (map.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid Antigravity workspace map.");
            foreach (var workspace in map.EnumerateObject()) await reader.AddReferenceAsync(workspace.Name, false, token).ConfigureAwait(false);
        }
    }
}
