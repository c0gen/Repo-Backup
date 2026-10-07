namespace RepoBackup.Core.Models;

public sealed record SourceMapping(string Id, string OriginalPath, string SnapshotPath, string RestoreFolder, SourceKind Kind);
public sealed record FileEntry(string Path, long Length, long ModifiedTicks, bool IsLink = false, bool IsDirectory = false);
public sealed record Preview(List<SourceMapping> Sources, List<FileEntry> Files, List<string> Excluded, List<string> Warnings, Coverage Coverage)
{
    public long Bytes => Files.Sum(f => f.Length);
    public long FileCount => Files.Count(f => !f.IsDirectory);
    public bool Complete => Warnings.Count == 0;
}

public sealed record RecoveryManifest
{
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

public sealed record SnapshotInfo(string Id, DateTimeOffset Time, string[] Tags, string[] Paths)
{
    public string? TagValue(string key) => Tags.FirstOrDefault(t => t.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..];
    public bool Successful => Tags.Contains("successful");
    public Coverage Coverage => TagValue("coverage") == "full" ? Coverage.FullProject : Coverage.PartialSelection;
}

public sealed record SnapshotFile(string Path, string Type, long Size);
public sealed record BackupProgress(string ProjectName, string Stage, double Fraction = 0, long Files = 0, long Bytes = 0, string? CurrentFile = null,
    bool IsIndeterminate = false, long? TotalBytes = null);
public sealed record VerificationRecord(string DestinationId, DateTimeOffset At, Outcome Outcome, string Message);
