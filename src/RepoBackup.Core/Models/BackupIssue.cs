namespace RepoBackup.Core.Models;

public enum BackupIssueSeverity { Advisory, CoverageError }

public sealed record BackupIssue(string Code, BackupIssueSeverity Severity, string? Path, string Message)
{
    public override string ToString() => Message;
}

public sealed record SourceChangeSummary(long Added, long Removed, long Modified, long MetadataChanged, List<string> Samples)
{
    public bool HasChanges => Added + Removed + Modified + MetadataChanged > 0;
    public override string ToString() => $"Source changes: {Added:N0} added, {Removed:N0} removed, {Modified:N0} modified, {MetadataChanged:N0} metadata changes."
        + (Samples.Count == 0 ? "" : "\n" + string.Join("\n", Samples));
}
