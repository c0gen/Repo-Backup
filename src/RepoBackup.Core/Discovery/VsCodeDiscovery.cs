using Microsoft.Data.Sqlite;
using RepoBackup.Core.Infrastructure;

namespace RepoBackup.Core.Discovery;

public sealed class VsCodeDiscovery : IProjectDiscoveryProvider
{
    private readonly List<string> userDataDirectories;
    public string Id { get; }
    private static readonly Dictionary<string, string> ExtensionProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["openai.chatgpt"] = DiscoveryProviders.Codex,
        ["anthropic.claude-code"] = DiscoveryProviders.ClaudeCode,
        ["github.copilot"] = DiscoveryProviders.Copilot,
        ["github.copilot-chat"] = DiscoveryProviders.Copilot
    };

    public VsCodeDiscovery(string? userDataDirectory = null, string source = DiscoveryProviders.VsCode)
        : this(userDataDirectory is null ? AppPaths.DefaultVsCodeUserDataDirectories : [userDataDirectory], source) { }

    public VsCodeDiscovery(IEnumerable<string> userDataDirectories, string source = DiscoveryProviders.VsCode)
    {
        if (source is not (DiscoveryProviders.VsCode or DiscoveryProviders.Codex or DiscoveryProviders.ClaudeCode or DiscoveryProviders.Copilot))
            throw new ArgumentException("Unknown VS Code discovery source.");
        Id = source; this.userDataDirectories = userDataDirectories.Select(PathSafety.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<ProviderDiscoveryResult> ReadAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var reader = new EditorWorkspaceReader(DiscoveryProviders.DisplayName(Id));
        foreach (var directory in userDataDirectories)
        {
            token.ThrowIfCancellationRequested();
            var userDirectory = EditorWorkspaceReader.UserDirectory(directory);
            var locations = new List<string> { userDirectory };
            try
            {
                foreach (var profile in Directory.EnumerateDirectories(Path.Combine(userDirectory, "profiles")))
                { token.ThrowIfCancellationRequested(); locations.Add(profile); }
            }
            catch (DirectoryNotFoundException) { }
            catch (Exception e) when (DiscoveryFiles.IsReadError(e)) { reader.Warn("profiles", e); }
            foreach (var location in locations)
            {
                await reader.ReadRegistryAsync(Path.Combine(location, "globalStorage", "state.vscdb"), null, null, token).ConfigureAwait(false);
                foreach (var workspace in await reader.ReadStorageAsync(Path.Combine(location, "workspaceStorage"), token).ConfigureAwait(false))
                {
                    var associations = await ReadAssociationsAsync(workspace.StorageDirectory, reader, token).ConfigureAwait(false);
                    if (workspace.Project is not null) reader.Projects.Add(workspace.Project with { AssociatedProviders = associations });
                    if (Id is DiscoveryProviders.VsCode or DiscoveryProviders.Copilot)
                        await ReadCopilotSessionsAsync(workspace.StorageDirectory, reader, token).ConfigureAwait(false);
                }
            }
        }
        var result = reader.Result();
        return Id == DiscoveryProviders.VsCode ? result : result with
        { Projects = result.Projects.Where(p => p.AssociatedProviders.Contains(Id)).Select(p => p with { AssociatedProviders = [] }).ToList() };
    }

    private static async Task<List<string>> ReadAssociationsAsync(string storage, EditorWorkspaceReader reader, CancellationToken token)
    {
        try
        {
            var keys = await DiscoverySqlite.ReadKeysAsync(Path.Combine(storage, "state.vscdb"), ExtensionProviders.Keys.ToList(), token).ConfigureAwait(false);
            return keys.Select(k => ExtensionProviders[k]).Distinct().ToList();
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return []; }
        catch (Exception e) when (DiscoveryFiles.IsReadError(e) || e is SqliteException) { reader.Warn("extension workspace state", e); return []; }
    }

    private static async Task ReadCopilotSessionsAsync(string storage, EditorWorkspaceReader reader, CancellationToken token)
    {
        try
        {
            foreach (var transcript in Directory.EnumerateFiles(Path.Combine(storage, "GitHub.copilot-chat", "transcripts"), "*.jsonl"))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (await CopilotSessionReader.ReadAsync(transcript, token).ConfigureAwait(false) is { } project) reader.Projects.Add(project);
                }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { }
                catch (Exception e) when (DiscoveryFiles.IsReadError(e)) { reader.Warn("Copilot session metadata", e); }
            }
        }
        catch (DirectoryNotFoundException) { }
        catch (Exception e) when (DiscoveryFiles.IsReadError(e)) { reader.Warn("Copilot session storage", e); }
    }
}
