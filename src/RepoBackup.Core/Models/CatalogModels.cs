namespace RepoBackup.Core.Models;

public enum Coverage { FullProject, PartialSelection }
public enum SourceKind { Project, Worktree, GitMetadata }
public enum Outcome { NotRun, Running, Successful, Failed, Incomplete, Cancelled, Interrupted }

public sealed record SourceRoot(string Id, string Path)
{
    public List<string> PreviousPaths { get; init; } = [];
    public List<string> Aliases { get; init; } = [];
}

public sealed record DiscoverySource(string ProviderId, string? ExternalId = null);

public sealed record ProjectEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string? ExternalId { get; init; }
    public string Name { get; init; } = "Project";
    public string Origin { get; init; } = "Manual";
    public List<DiscoverySource> DiscoverySources { get; init; } = [];
    public List<SourceRoot> Roots { get; init; } = [];
    public bool Reviewed { get; init; }
    public bool Enabled { get; init; }
    public bool Dismissed { get; init; }
    public DateTimeOffset DiscoveredAt { get; init; } = DateTimeOffset.UtcNow;
    public ExclusionRules Exclusions { get; init; } = new();
}

public sealed record ExclusionRules
{
    public static readonly string[] Defaults = ["node_modules", ".venv", "venv", "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".next", ".turbo", ".gradle", ".cache", "obj"];
    public List<string> ExcludedDirectories { get; init; } = [.. Defaults];
    public List<string> RelativePatterns { get; init; } = [];
}

public sealed record SavedSelection
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string ProjectId { get; init; }
    public required string Name { get; init; }
    public List<string> Paths { get; init; } = [];
    public ExclusionRules Exclusions { get; init; } = new();
}

public sealed record Destination
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Name { get; init; }
    public required string Path { get; init; }
    public string? RepositoryId { get; init; }
    public DateTimeOffset? VerifiedAt { get; init; }
}

public sealed record JobRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public required string SeriesId { get; init; }
    public required string DestinationId { get; init; }
    public Coverage Coverage { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; init; }
    public Outcome Backup { get; init; } = Outcome.Running;
    public Outcome Verification { get; init; }
    public Outcome Cleanup { get; init; }
    public string? SnapshotId { get; init; }
    public long Files { get; init; }
    public long Bytes { get; init; }
    public List<string> Messages { get; init; } = [];
    public int OwnerProcessId { get; init; } = Environment.ProcessId;
    public long OwnerStartTicks { get; init; } = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
}

public enum ScheduleKind { Daily, Weekly, Interval }
public sealed record ScheduleDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "Project backups";
    public required string DestinationId { get; init; }
    public string? SelectionId { get; init; }
    public bool Enabled { get; init; }
    public ScheduleKind Kind { get; init; }
    public TimeOnly Time { get; init; } = new(18, 0);
    public DayOfWeek Day { get; init; } = DayOfWeek.Friday;
    public int IntervalHours { get; init; } = 24;
}

public sealed record CatalogExport(int SchemaVersion, List<ProjectEntry> Projects, List<SavedSelection> Selections, List<Destination> Destinations, List<JobRecord> History, List<ScheduleDefinition> Schedules);
