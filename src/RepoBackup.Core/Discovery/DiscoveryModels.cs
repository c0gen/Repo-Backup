namespace RepoBackup.Core.Discovery;

public static class DiscoveryProviders
{
    public const string Codex = "codex";
    public const string ClaudeCode = "claude-code";
    public const string Antigravity = "antigravity";
    public const string VsCode = "vscode";
    public const string Copilot = "copilot";
    public static readonly string[] All = [Codex, ClaudeCode, Antigravity, VsCode, Copilot];
    public static readonly string[] HookCapable = [Codex, ClaudeCode, Antigravity];

    public static string DisplayName(string provider) => provider switch
    {
        Codex => "Codex", ClaudeCode => "Claude Code", Antigravity => "Antigravity",
        VsCode => "VS Code", Copilot => "GitHub Copilot",
        "manual" => "Manual", "repository-scan" => "Repository scan", _ => provider
    };

    public static string FromOrigin(string origin) => origin switch
    {
        "Codex" or "Codex SessionStart" => Codex,
        "Claude Code" or "Claude Code SessionStart" => ClaudeCode,
        "Antigravity" or "Antigravity PostInvocation" => Antigravity,
        "VS Code" => VsCode, "GitHub Copilot" => Copilot,
        _ => origin.Trim().ToLowerInvariant().Replace(' ', '-')
    };
}

public sealed record DiscoveredProject(string ExternalId, string Name, List<string> Roots)
{
    // Transient associations; only existing provider bindings enter the catalog.
    public List<string> AssociatedProviders { get; init; } = [];
}
public sealed record ProviderDiscoveryResult(List<DiscoveredProject> Projects, List<string> Warnings);
public sealed record DiscoveryResult(int Added, int Projects, int Roots, List<string> Warnings);

public interface IProjectDiscoveryProvider
{
    string Id { get; }
    Task<ProviderDiscoveryResult> ReadAsync(CancellationToken token = default);
}

public sealed record DiscoveryOptions
{
    public string Source { get; init; } = "all";
    public string? CodexStatePath { get; init; }
    public string? CodexHomeDirectory { get; init; }
    public string? ClaudeConfigPath { get; init; }
    public string? ClaudeProjectsDirectory { get; init; }
    public string? AntigravityUserDataDirectory { get; init; }
    public string? VsCodeUserDataDirectory { get; init; }
}
