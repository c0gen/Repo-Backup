using System.Text.Json;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Backup;

// Optional restic telemetry is deliberately independent of snapshot success/finalization.
public sealed class BackupOutputProgress(string projectName, string stage, long totalBytes, long totalFiles,
    string stagingDirectory, IProgress<BackupProgress>? progress)
{
    private long bytes, files;
    private double fraction;
    public string? SnapshotId { get; private set; }

    public void OnOutput(string line)
    {
        try
        {
            using var json = JsonDocument.Parse(line);
            var value = json.RootElement;
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("message_type", out var type) || type.ValueKind != JsonValueKind.String) return;
            if (type.GetString() == "summary")
            {
                if (value.TryGetProperty("snapshot_id", out var id) && id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString()))
                    SnapshotId = id.GetString();
                return;
            }
            if (type.GetString() != "status") return;
            bytes = Math.Max(bytes, Math.Min(Math.Max(0, totalBytes), Count(value, "bytes_done")));
            files = Math.Max(files, Math.Min(Math.Max(0, totalFiles), Count(value, "files_done")));
            string? path = null;
            if (value.TryGetProperty("current_files", out var current) && current.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in current.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())) continue;
                    var candidate = item.GetString()!;
                    try { if (PathSafety.IsWithin(candidate, stagingDirectory)) continue; }
                    catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { continue; }
                    path = candidate; break;
                }
            }
            var measured = totalBytes > 0 ? (double)bytes / totalBytes : totalFiles > 0 ? (double)files / totalFiles : 0;
            fraction = Math.Max(fraction, Math.Clamp(measured, 0, .99));
            progress?.Report(new(projectName, stage, fraction, files, bytes, path,
                totalBytes <= 0 && totalFiles <= 0, Math.Max(0, totalBytes), Math.Max(0, totalFiles)));
        }
        catch (JsonException) { /* Malformed telemetry must not interrupt a backup. */ }
    }

    private static long Count(JsonElement value, string name) => value.TryGetProperty(name, out var count)
        && count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out var number) ? Math.Max(0, number) : 0;
}
