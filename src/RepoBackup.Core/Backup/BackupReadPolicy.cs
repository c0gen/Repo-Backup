using System.Globalization;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Backup;

public sealed record BackupReadDecision(BackupReadMode Mode, string? ParentId, DateTimeOffset FullReadAt);

public static class BackupReadPolicy
{
    public const string Setting = "backupReadMode";
    public static BackupReadDecision Select(IEnumerable<SnapshotSummary> snapshots, string series, Coverage coverage,
        string hostname, BackupReadMode requested, DateTimeOffset now, IReadOnlySet<string>? seriesAliases = null)
    {
        if (!Enum.IsDefined(requested)) throw new ArgumentException("Unsupported backup read mode.");
        var parent = snapshots.Where(s => s.Successful && !s.Tags.Contains("pending") && (seriesAliases?.Contains(s.TagValue("series") ?? "") ?? s.TagValue("series") == series)
            && s.Coverage == coverage && string.Equals(s.Hostname, hostname, StringComparison.OrdinalIgnoreCase) && s.Time <= now)
            .OrderByDescending(s => s.Time).FirstOrDefault();
        if (requested == BackupReadMode.Incremental && parent is not null
            && long.TryParse(parent.TagValue("full-read-at"), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            && ticks >= DateTimeOffset.MinValue.UtcTicks && ticks <= DateTimeOffset.MaxValue.UtcTicks)
        {
            var last = new DateTimeOffset(ticks, TimeSpan.Zero);
            if (last <= parent.Time && last <= now && now - last < TimeSpan.FromDays(7))
                return new(BackupReadMode.Incremental, parent.Id, last);
        }
        return new(BackupReadMode.Full, null, now);
    }
}
