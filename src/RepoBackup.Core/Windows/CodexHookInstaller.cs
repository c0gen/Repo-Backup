using RepoBackup.Core.Discovery;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Storage;

namespace RepoBackup.Core.Windows;

public static class CodexHookInstaller
{
    public static string Merge(string? existing, string runnerPath, string? dataDirectory = null)
    {
        var legacyCommand = "powershell.exe -NoLogo -NoProfile -NonInteractive -Command \"& '" + runnerPath.Replace("'", "''", StringComparison.Ordinal) + "' register-hook\"";
        return SessionStartHookMerger.Merge(existing, HookCommandBuilder.Build(runnerPath, DiscoveryProviders.Codex, dataDirectory), legacyCommand);
    }

    public static void Install(string? codexHome = null, string? dataDirectory = null)
    {
        var paths = new AppPaths(dataDirectory);
        DiscoveryHookInstaller.Install(DiscoveryProviders.Codex, new CatalogStore(paths), paths.DataDirectory,
            configurationPath: Path.Combine(codexHome ?? AppPaths.DefaultCodexHome, "hooks.json"));
    }
}
