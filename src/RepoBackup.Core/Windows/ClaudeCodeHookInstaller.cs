using RepoBackup.Core.Discovery;

namespace RepoBackup.Core.Windows;

public static class ClaudeCodeHookInstaller
{
    public static string Merge(string? existing, string runnerPath, string? dataDirectory = null) =>
        SessionStartHookMerger.Merge(existing, HookCommandBuilder.Build(runnerPath, DiscoveryProviders.ClaudeCode, dataDirectory));
}
