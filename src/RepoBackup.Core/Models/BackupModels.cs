namespace RepoBackup.Core.Models;

public enum BackupReadMode { Incremental, Full }

public sealed record SourceMapping(string Id, string OriginalPath, string SnapshotPath, string RestoreFolder, SourceKind Kind);
public sealed record FileEntry(string Path, long Length, long ModifiedTicks, bool IsLink = false, bool IsDirectory = false, string? WslFingerprint = null);
public sealed record ManifestDirectory(string SourceId, string RelativePath);
public sealed record Preview(List<SourceMapping> Sources, List<FileEntry> Files, List<string> Excluded, List<string> Warnings, Coverage Coverage)
{
    public List<WslLink> WslLinks { get; init; } = [];
    public List<ManifestDirectory> Directories { get; init; } = [];
    public List<string> GitMetadataDirectories { get; init; } = [];
    public List<GitContext> GitContexts { get; init; } = [];
    public List<GitRegistration> GitRegistrations { get; init; } = [];
    public List<BackupIssue> Issues { get; init; } = [];
    public long Bytes => Files.Sum(f => f.Length);
    public long FileCount => Files.Count(f => !f.IsDirectory);
    public bool Complete => Warnings.Count == 0 && Issues.All(i => i.Severity != BackupIssueSeverity.CoverageError);
}

public sealed record RecoveryManifest
{
    public List<GitContext> GitContexts { get; init; } = [];
    public List<GitRegistration> GitRegistrations { get; init; } = [];
    public List<BackupIssue> Issues { get; init; } = [];
    public List<WslLink> WslLinks { get; init; } = [];
    public List<ManifestDirectory> Directories { get; init; } = [];
    public List<string> GitMetadataDirectories { get; init; } = [];
    public int SchemaVersion { get; init; } = 1;
    public required string JobId { get; init; }
    public required string SeriesId { get; init; }
    public required ProjectEntry Project { get; init; }
    public SavedSelection? Selection { get; init; }
    public Coverage Coverage { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<SourceMapping> Sources { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public record SnapshotSummary(string Id, DateTimeOffset Time, string[] Tags)
{
    public string? Hostname { get; init; }
    public string? TagValue(string key) => Tags.FirstOrDefault(t => t.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..];
    public bool Successful => Tags.Contains("successful");
    public Coverage Coverage => TagValue("coverage") == "full" ? Coverage.FullProject : Coverage.PartialSelection;
}

public sealed record SnapshotInfo(string Id, DateTimeOffset Time, string[] Tags, string[] Paths) : SnapshotSummary(Id, Time, Tags);

public sealed record SnapshotFile(string Path, string Type, long Size);
public sealed record BackupProgress(string ProjectName, string Stage, double Fraction = 0, long Files = 0, long Bytes = 0, string? CurrentFile = null,
    bool IsIndeterminate = false, long? TotalBytes = null, long? TotalFiles = null, string? RequestId = null, string? RunId = null);
public sealed record SourceScanProgress(string CurrentPath, long Files, long Bytes);
public sealed record VerificationRecord(string DestinationId, DateTimeOffset At, Outcome Outcome, string Message);
