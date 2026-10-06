using RepoBackup.Core.Discovery;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Storage;

namespace RepoBackup.Core.Windows;

public sealed record HookInstallation(string ProviderId, string ConfigurationPath, DateTimeOffset ConfiguredAt);

public static class DiscoveryHookInstaller
{
    public static string ConfigurationPath(string provider) => provider switch
    {
        DiscoveryProviders.Codex => Path.Combine(AppPaths.DefaultCodexHome, "hooks.json"),
        DiscoveryProviders.ClaudeCode => Path.Combine(AppPaths.DefaultClaudeHome, "settings.json"),
        DiscoveryProviders.Antigravity => AppPaths.DefaultAntigravityHooksPath,
        _ => throw new ArgumentException("Choose hook source codex, claude-code, or antigravity.")
    };

    public static HookInstallation Install(string provider, CatalogStore catalog, string dataDirectory,
        string? runnerPath = null, string? configurationPath = null)
    {
        var defaultConfiguration = ConfigurationPath(provider);
        runnerPath = PathSafety.Normalize(runnerPath ?? WindowsScheduler.RunnerPath);
        if (!File.Exists(runnerPath)) throw new FileNotFoundException("Install the packaged app first to use its stable hook runner.");
        var path = PathSafety.Normalize(configurationPath ?? defaultConfiguration);
        var changed = HookConfigurationWriter.Update(path, existing => provider switch
        {
            DiscoveryProviders.Codex => CodexHookInstaller.Merge(existing, runnerPath, dataDirectory),
            DiscoveryProviders.ClaudeCode => ClaudeCodeHookInstaller.Merge(existing, runnerPath, dataDirectory),
            DiscoveryProviders.Antigravity => AntigravityHookInstaller.Merge(existing, runnerPath, dataDirectory),
            _ => throw new ArgumentException("Unknown hook provider.")
        });
        var previous = catalog.GetSetting<HookInstallation>("hook-installation:" + provider);
        var installation = !changed && previous?.ConfigurationPath == path ? previous : new HookInstallation(provider, path, DateTimeOffset.UtcNow);
        catalog.SaveSetting("hook-installation:" + provider, installation); return installation;
    }

    public static string Status(CatalogStore catalog, string provider)
    {
        var installation = catalog.GetSetting<HookInstallation>("hook-installation:" + provider);
        var lastEvent = catalog.GetSetting<DateTimeOffset?>("hook-last-registration:" + provider);
        if (lastEvent is { } at && (installation is null || at >= installation.ConfiguredAt))
            return "Verified · last registration " + at.ToLocalTime().ToString("MMM d, h:mm tt");
        return installation is null ? "Not configured" : "Configured · awaiting first successful event";
    }

    public static string Instructions(string provider) => provider switch
    {
        DiscoveryProviders.Codex => "Review and trust the hook through Codex /hooks, then start or resume a session to verify registration.",
        DiscoveryProviders.ClaudeCode => "Review the hook in Claude Code /hooks, then start or resume a session to verify registration.",
        DiscoveryProviders.Antigravity => "Requires an Antigravity build with native Hooks support. Review Customizations > Hooks, then send a prompt to verify PostInvocation registration.",
        _ => throw new ArgumentException("Unknown hook provider.")
    };
}
