using System.Windows.Media;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Backup;
using RepoBackup.Core.Discovery;
using RepoBackup.Core.Models;
using RepoBackup.Desktop.Infrastructure;

namespace RepoBackup.Desktop.ViewModels;

public sealed class ProjectItem : ObservableObject
{
    private ProjectEntry project;
    private readonly Action<ProjectEntry> save;
    private bool selected;
    public ProjectEntry Project => project;
    public string Id => project.Id;
    public string Name => project.Name;
    public string Paths => string.Join("  ·  ", project.Roots.Select(r => r.Path));
    public string Programs => project.DiscoverySources.Count == 0 ? project.Origin : string.Join(" · ", project.DiscoverySources
        .Select(s => DiscoveryProviders.DisplayName(s.ProviderId)).Distinct().Order(StringComparer.OrdinalIgnoreCase));
    public bool Matches(string query) => Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Paths.Contains(query, StringComparison.OrdinalIgnoreCase) || Programs.Contains(query, StringComparison.OrdinalIgnoreCase);
    public bool IsBroad => project.Roots.Any(r => PathSafety.IsBroad(r.Path));
    public string DiscoveryDetail => $"{Programs} · {project.Roots.Count} source root{(project.Roots.Count == 1 ? "" : "s")}" + (IsBroad ? " · Broad location — review contents carefully" : "");
    public bool Available { get; }
    public DateTimeOffset? LastBackup { get; private set; }
    public bool NeedsBackup { get; private set; }
    public string LastBackupText => LastBackup is { } at ? at.LocalDateTime.ToString("MMM d, h:mm tt") : "—";
    public string Status { get; private set; } = "Not backed up";
    public Brush StatusBrush { get; private set; } = Brushes.Orange;
    public bool IsSelected { get => selected; set => Set(ref selected, value); }
    public bool Enabled
    {
        get => project.Enabled;
        set { if (project.Enabled == value) return; var changed = project with { Enabled = value }; save(changed); project = changed; Raise(); }
    }
    public ProjectItem(ProjectEntry project, IEnumerable<JobRecord> history, Action<ProjectEntry> save, bool? available = null)
    {
        this.project = project; this.save = save; Available = available ?? project.Roots.All(r => Directory.Exists(r.Path));
        UpdateProtection(history, null);
    }
    public void UpdateProtection(IEnumerable<JobRecord> history, string? destinationId)
    {
        var protection = ProjectProtection.For(Id, destinationId, history);
        var last = protection.LatestAttempt;
        LastBackup = protection.LastBackup; NeedsBackup = protection.NeedsBackup;
        Status = !Available ? "Drive unavailable" : last?.Backup switch { Outcome.Running => "Backing up", Outcome.Incomplete => "Incomplete coverage", Outcome.Failed => "Last backup failed", Outcome.Interrupted => "Interrupted", Outcome.Cancelled => "Cancelled", Outcome.Successful when last.Verification == Outcome.Successful => last.Cleanup is Outcome.Failed or Outcome.Cancelled or Outcome.Interrupted ? "Ready · cleanup " + last.Cleanup.ToString().ToLowerInvariant() : "Ready", Outcome.Successful => "Verification " + last.Verification.ToString().ToLowerInvariant(), _ => "Not backed up" };
        StatusBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Status.StartsWith("Ready") ? "#79D994" : Status == "Backing up" ? "#65ADFF" : "#FFC361"));
        foreach (var property in new[] { nameof(LastBackup), nameof(LastBackupText), nameof(NeedsBackup), nameof(Status), nameof(StatusBrush) }) Raise(property);
    }
}

public sealed record SelectionOption(string Name, SavedSelection? Selection)
{
    public override string ToString() => Name;
}
public sealed record SourceNode(string Name, string Detail, string Icon, IReadOnlyList<SourceNode> Children);
public sealed record SnapshotItem(SnapshotInfo Snapshot)
{
    public string Name { get { try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(Snapshot.TagValue("name") ?? "")); } catch (FormatException) { return "Unknown project"; } } }
    public string Date => Snapshot.Time.LocalDateTime.ToString("MMM d, yyyy · h:mm tt");
    public string Coverage => Snapshot.Coverage == Core.Models.Coverage.FullProject ? "Full project" : "Partial selection";
    public string Status => Snapshot.Successful ? "Successful" : "Incomplete / interrupted";
    public Outcome Outcome => Snapshot.Successful ? Outcome.Successful : Outcome.Incomplete;
    public string ShortId => Snapshot.Id[..8];
}
