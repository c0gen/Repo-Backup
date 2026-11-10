using System.Text.Json;
using RepoBackup.Core.Application;
using RepoBackup.Core.Backup;
using RepoBackup.Core.Models;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class BackupProgressTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        await tests.Run("Backup telemetry reports large-file bytes, bounds counters, and hides staging paths", () =>
        {
            var recorder = new ProgressRecorder(); var staging = Path.Combine(root, "staging");
            var progress = new BackupOutputProgress("Project", "Backing up · live", 1000, 10, staging, recorder);
            var path = Path.Combine(root, "résumé-文件[1].txt");
            progress.OnOutput(Status(450, 1, [Path.Combine(staging, "manifest.json"), path]));
            Assert(recorder.Latest.Bytes == 450 && recorder.Latest.Files == 1 && recorder.Latest.Fraction == .45
                && recorder.Latest.TotalBytes == 1000 && recorder.Latest.TotalFiles == 10 && recorder.Latest.CurrentFile == path, "Backup status lost its inventory totals or source filename.");
            progress.OnOutput(Status(20, 0, []));
            Assert(recorder.Latest.Bytes == 450 && recorder.Latest.Fraction == .45 && recorder.Latest.CurrentFile is null, "Late counters regressed or filename was stale.");
            progress.OnOutput(Status(long.MaxValue, long.MaxValue, [Path.Combine(staging, "manifest.json")]));
            Assert(recorder.Latest.Bytes == 1000 && recorder.Latest.Files == 10 && recorder.Latest.Fraction == .99 && recorder.Latest.CurrentFile is null, "Progress was unbounded or exposed staging.");
            progress.OnOutput("{\"message_type\":\"summary\",\"snapshot_id\":\"snapshot\"}");
            Assert(progress.SnapshotId == "snapshot" && recorder.Items.All(p => p.Fraction < 1), "Summary claimed verified success.");
            return Task.CompletedTask;
        });
        await tests.Run("Backup telemetry tolerates malformed optional data and supports zero-byte files", () =>
        {
            var recorder = new ProgressRecorder(); var progress = new BackupOutputProgress("Project", "Backing up", 0, 2, root, recorder);
            foreach (var line in new[] { "not json", "{", "null", "[]", "42", "{}", "{\"message_type\":42}",
                "{\"message_type\":\"future\"}", "{\"message_type\":\"status\",\"bytes_done\":\"bad\",\"files_done\":1e99,\"current_files\":42}",
                "{\"message_type\":\"status\",\"bytes_done\":-1,\"files_done\":-2,\"current_files\":[42,null,\"\"]}",
                "{\"message_type\":\"summary\",\"snapshot_id\":42}" }) progress.OnOutput(line);
            Assert(progress.SnapshotId is null && recorder.Latest.Bytes == 0 && recorder.Latest.Files == 0 && recorder.Latest.CurrentFile is null, "Malformed telemetry changed counters or snapshot identity.");
            progress.OnOutput(Status(0, 1, []));
            Assert(recorder.Latest.Fraction == .5 && !recorder.Latest.IsIndeterminate, "Zero-byte files have no measurable progress.");
            var empty = new BackupOutputProgress("Empty", "Backing up", 0, 0, root, recorder); empty.OnOutput(Status(0, 0, []));
            Assert(recorder.Latest.IsIndeterminate && recorder.Latest.Fraction == 0, "Unknown totals implied completion.");
            return Task.CompletedTask;
        });
        await tests.Run("Source inventory reports activity, totals, and cancellation without flooding callbacks", async () =>
        {
            var app = new ApplicationServices(Path.Combine(root, "catalog"), discoveryProviders: []);
            var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
            for (var i = 0; i < 1200; i++) await File.WriteAllTextAsync(Path.Combine(source, "file-" + i + ".txt"), "inventory");
            var project = app.Catalog.RegisterCandidate("Inventory", [source], "Manual", approved: true);
            var updates = new List<SourceScanProgress>();
            var preview = await app.Planner.PreviewAsync(project, progress: new ScanCallback(updates.Add));
            Assert(updates.Count >= 2 && updates.Count < 100 && updates[^1].Files == preview.FileCount && updates[^1].Bytes == preview.Bytes
                && updates.Any(p => p.CurrentPath.StartsWith(source)), "Inventory had no useful or bounded progress.");
            using var cancellation = new CancellationTokenSource();
            await Throws<OperationCanceledException>(() => app.Planner.PreviewAsync(project, token: cancellation.Token,
                progress: new ScanCallback(_ => cancellation.Cancel())));
        });
    }

    private static string Status(long bytes, long files, string[] paths) => JsonSerializer.Serialize(new
    { message_type = "status", bytes_done = bytes, files_done = files, current_files = paths });
    private sealed class ScanCallback(Action<SourceScanProgress> report) : IProgress<SourceScanProgress>
    { public void Report(SourceScanProgress value) => report(value); }
}
