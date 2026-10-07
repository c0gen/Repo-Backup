using System.Text.Json;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Recovery;

// Restic reports counters per process; a restore can run one process per source.
public sealed class RestoreProgress
{
    private readonly string projectName;
    private readonly IProgress<BackupProgress>? progress;
    private readonly long totalBytes, totalItems;
    private RestorePathPlan? plan;
    private Dictionary<string, SnapshotFile> expected = [];
    private readonly HashSet<string> finished = new(StringComparer.Ordinal);
    private long completedBytes, completedItems, sourceBytes, statusBytes, statusItems, eventBytes;
    private double fraction;
    private string? currentFile;
    private bool verifying;

    public RestoreProgress(string projectName, IReadOnlyList<RestorePathPlan> plans, IProgress<BackupProgress>? progress)
    {
        this.projectName = projectName;
        this.progress = progress;
        totalBytes = plans.Sum(Bytes);
        totalItems = plans.Sum(p => (long)p.Expected.Count);
    }

    private static long Bytes(RestorePathPlan source) => source.Expected.Where(n => n.Type == "file").Sum(n => Math.Max(0, n.Size));

    public void Begin(RestorePathPlan source)
    {
        plan = source;
        expected = source.Expected.ToDictionary(n => "/" + n.Path[source.Tree.Length..].TrimStart('/'), StringComparer.Ordinal);
        sourceBytes = Bytes(source);
        statusBytes = statusItems = eventBytes = 0;
        finished.Clear();
        currentFile = null;
        verifying = false;
        Report("Restoring " + source.Source.RestoreFolder, totalBytes == 0 && totalItems == 0);
    }

    public void OnOutput(string line)
    {
        if (plan is null || verifying) return;
        try
        {
            using var json = JsonDocument.Parse(line);
            var value = json.RootElement;
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("message_type", out var type) || type.ValueKind != JsonValueKind.String) return;
            switch (type.GetString())
            {
                case "status":
                    UpdateCounters(value);
                    Report("Restoring " + plan.Source.RestoreFolder);
                    break;
                case "verbose_status":
                    if (!value.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.String || action.GetString() is not ("restored" or "updated")) return;
                    if (!value.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.String) return;
                    var path = "/" + item.GetString()!.Replace('\\', '/').TrimStart('/');
                    if (!expected.TryGetValue(path, out var node)) return;
                    if (finished.Add(path) && node.Type == "file") eventBytes += Math.Max(0, node.Size);
                    if (node.Type == "file") currentFile = plan.Source.RestoreFolder + "\\" + path.TrimStart('/').Replace('/', '\\');
                    Report("Restoring " + plan.Source.RestoreFolder);
                    break;
                case "summary":
                    UpdateCounters(value);
                    verifying = true;
                    // --verify runs after this summary and can still fail.
                    Phase("Verifying " + plan.Source.RestoreFolder);
                    break;
            }
        }
        catch (JsonException) { /* Progress telemetry must not interrupt recovery. */ }
    }

    private void UpdateCounters(JsonElement value)
    {
        statusBytes = Math.Max(statusBytes, Math.Min(sourceBytes, Count(value, "bytes_restored")));
        statusItems = Math.Max(statusItems, Math.Min(plan!.Expected.Count, Count(value, "files_restored")));
    }

    private static long Count(JsonElement value, string property) => value.TryGetProperty(property, out var count)
        && count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out var number) ? Math.Max(0, number) : 0;

    public void Phase(string stage)
    {
        currentFile = null;
        Report(stage, true);
    }

    public void CompleteSource()
    {
        completedBytes += sourceBytes;
        completedItems += plan!.Expected.Count;
        plan = null;
        statusBytes = statusItems = eventBytes = 0;
        finished.Clear();
    }

    public void Complete() => progress?.Report(new(projectName, "Restore completed", 1, totalItems, totalBytes, TotalBytes: totalBytes));

    private void Report(string stage, bool indeterminate = false)
    {
        var bytes = completedBytes + Math.Max(statusBytes, eventBytes);
        var items = completedItems + Math.Max(statusItems, finished.Count);
        var measured = totalBytes > 0 ? (double)bytes / totalBytes : totalItems > 0 ? (double)items / totalItems : 0;
        fraction = Math.Max(fraction, Math.Clamp(measured, 0, .99));
        progress?.Report(new(projectName, stage, fraction, items, bytes, currentFile, indeterminate, totalBytes));
    }
}
