namespace RepoBackup.Core.Infrastructure;

public sealed class AppPaths
{
    public string DataDirectory { get; }
    public string DatabasePath => Path.Combine(DataDirectory, "catalog.db");
    public string CredentialsDirectory => Path.Combine(DataDirectory, "credentials");
    public string JobsDirectory => Path.Combine(DataDirectory, "jobs");
    public string CacheDirectory => Path.Combine(DataDirectory, "cache");
    public string LocksDirectory => Path.Combine(DataDirectory, "locks");
    public static string DefaultDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RepoBackup");
    public static string InstalledDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "RepoBackup");
    public static string DefaultCodexHome => Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    public static string DefaultClaudeHome => Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    public static string DefaultClaudeConfigPath => Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } directory
        ? Path.Combine(directory, ".claude.json") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
    public static string DefaultAntigravityUserDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Antigravity");
    public static string DefaultAntigravityHooksPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "config", "hooks.json");

    public AppPaths(string? dataDirectory = null)
    {
        DataDirectory = Path.GetFullPath(dataDirectory ?? DefaultDataDirectory);
        foreach (var directory in new[] { DataDirectory, CredentialsDirectory, JobsDirectory, CacheDirectory, LocksDirectory }) Directory.CreateDirectory(directory);
    }
}
