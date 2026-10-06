using Microsoft.Data.Sqlite;
using RepoBackup.Core.Storage;

namespace RepoBackup.Core.Discovery;

public sealed class ProjectDiscoveryService(CatalogStore catalog, IEnumerable<IProjectDiscoveryProvider> providers)
{
    private readonly List<IProjectDiscoveryProvider> providers = providers.ToList();
    private readonly RepositoryScanner scanner = new(catalog);

    public static List<IProjectDiscoveryProvider> CreateProviders(DiscoveryOptions? options = null)
    {
        options ??= new();
        if (options.Source != "all" && !DiscoveryProviders.All.Contains(options.Source))
            throw new ArgumentException("Choose source codex, claude-code, antigravity, vscode, copilot, or all.");
        var result = new List<IProjectDiscoveryProvider>();
        if (options.Source is "all" or DiscoveryProviders.Codex) result.Add(new CodexDiscovery(options.CodexStatePath, options.CodexHomeDirectory));
        if (options.Source is "all" or DiscoveryProviders.ClaudeCode) result.Add(new ClaudeCodeDiscovery(options.ClaudeConfigPath, options.ClaudeProjectsDirectory));
        if (options.Source is "all" or DiscoveryProviders.Antigravity) result.Add(new AntigravityDiscovery(options.AntigravityUserDataDirectory));
        if (options.Source is "all" or DiscoveryProviders.VsCode or DiscoveryProviders.Copilot)
            result.Add(new VsCodeDiscovery(options.VsCodeUserDataDirectory, options.Source == "all" ? DiscoveryProviders.VsCode : options.Source));
        // Explicit native metadata overrides must remain isolated from real editor
        // state. The editor override can opt extension discovery back in.
        else if (options.Source == DiscoveryProviders.Codex && (options.VsCodeUserDataDirectory is not null || (options.CodexStatePath is null && options.CodexHomeDirectory is null)))
            result.Add(new VsCodeDiscovery(options.VsCodeUserDataDirectory, DiscoveryProviders.Codex));
        else if (options.Source == DiscoveryProviders.ClaudeCode && (options.VsCodeUserDataDirectory is not null || (options.ClaudeConfigPath is null && options.ClaudeProjectsDirectory is null)))
            result.Add(new VsCodeDiscovery(options.VsCodeUserDataDirectory, DiscoveryProviders.ClaudeCode));
        return result;
    }

    public Task<DiscoveryResult> RefreshAsync(string statePath, CancellationToken token = default) =>
        RefreshAsync(new DiscoveryOptions { Source = DiscoveryProviders.Codex, CodexStatePath = statePath }, token);

    public Task<DiscoveryResult> RefreshAsync(DiscoveryOptions? options = null, CancellationToken token = default) => Task.Run(async () =>
    {
        var warnings = new List<string>(); var added = 0;
        foreach (var provider in options is null ? providers : CreateProviders(options))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var result = await provider.ReadAsync(token).ConfigureAwait(false); warnings.AddRange(result.Warnings);
                // Prefer a program's complete workspace over its duplicate single-folder history records.
                foreach (var candidate in result.Projects.OrderByDescending(p => p.Roots.Count))
                {
                    token.ThrowIfCancellationRequested();
                    foreach (var source in candidate.AssociatedProviders.Prepend(provider.Id).Distinct())
                        added += catalog.RegisterDiscovered(candidate.Name, candidate.Roots, source, candidate.ExternalId).Added;
                }
            }
            catch (Exception e) when (DiscoveryFiles.IsReadError(e) || e is SqliteException)
            { warnings.Add(DiscoveryFiles.Warning(DiscoveryProviders.DisplayName(provider.Id), e)); }
        }
        var all = catalog.Projects();
        return new DiscoveryResult(added, all.Count, all.Sum(p => p.Roots.Count), warnings.Distinct().ToList());
    }, token);

    public Task<int> ScanAsync(string location, CancellationToken token = default) => scanner.ScanAsync(location, token);
}
