namespace RepoBackup.Core.Models;

public sealed record GitContext(string WorkingPath, string GitDirectory, string CommonDirectory);
public sealed record GitWorktreeRecord(string Path, string? Head, string? Branch, bool Locked, string? PrunableReason);
public sealed record GitRegistration(string CommonDirectory, string AdministrativeDirectory, string WorktreePath, bool Available, string? Reason);

public sealed class GitInventory
{
    public List<GitContext> Contexts { get; } = [];
    public List<GitRegistration> Registrations { get; } = [];
    public List<(string Path, SourceKind Kind)> Sources { get; } = [];
    public List<BackupIssue> Issues { get; } = [];
    public HashSet<string> TrackedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
}
