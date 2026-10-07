using System.Text.Json;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using RepoBackup.Core.Recovery;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class RestoreProgressTests
{
    public static async Task RunAsync(TestRunner tests)
    {
        await tests.Run("Restore progress aggregates source bytes without resets or double counting", () =>
        {
            var first = Plan("first", 100); var second = Plan("second", 900);
            var recorded = new ProgressRecorder(); var progress = new RestoreProgress("Project", [first, second], recorded);
            progress.Begin(first);
            progress.OnOutput(Status(50, 1)); Assert(recorded.Latest.Fraction == .05, "Progress was scoped to one source.");
            progress.OnOutput(Item("/file.txt")); Assert(recorded.Latest.Fraction == .1 && recorded.Latest.Bytes == 100, "File events double counted status bytes.");
            Assert(recorded.Latest.CurrentFile == "first\\file.txt", "Filename was not relative to the restored source.");
            progress.OnOutput(Status(20, 0)); Assert(recorded.Latest.Fraction == .1, "Late status moved progress backwards.");
            progress.OnOutput(Summary(100, 2)); Assert(recorded.Latest.IsIndeterminate && recorded.Latest.CurrentFile is null, "Summary claimed completion before verification.");
            progress.OnOutput(Status(100, 2)); Assert(recorded.Latest.Stage.StartsWith("Verifying"), "Late status exited verification.");
            progress.CompleteSource(); progress.Begin(second);
            Assert(recorded.Latest.Fraction == .1 && recorded.Latest.Bytes == 100, "New source reset progress.");
            progress.OnOutput(Status(450, 1)); Assert(recorded.Latest.Fraction == .55, "Large-file byte progress was lost.");
            progress.OnOutput(Summary(900, 2)); progress.CompleteSource(); progress.Phase("Finalizing restore");
            Assert(recorded.Items.All(p => p.Fraction < 1), "Restore reached 100% before finalization.");
            progress.Complete(); recorded.AssertSuccessfulRestore();
            return Task.CompletedTask;
        });
        await tests.Run("Selected file and folder progress excludes unrelated snapshot contents", () =>
        {
            var source = Source("selected");
            SnapshotFile[] nodes = [new(source.SnapshotPath, "dir", 0), new(source.SnapshotPath + "/folder", "dir", 0),
                new(source.SnapshotPath + "/folder/file.txt", "file", 200), new(source.SnapshotPath + "/unrelated.txt", "file", 800)];
            foreach (var selected in new[] { source.SnapshotPath + "/folder", source.SnapshotPath + "/folder/file.txt" })
            {
                var plan = RestorePaths.Plan(source, nodes, selected); var recorded = new ProgressRecorder();
                var progress = new RestoreProgress("Project", [plan], recorded); progress.Begin(plan);
                progress.OnOutput(Item("/unrelated.txt")); Assert(recorded.Latest.Bytes == 0 && recorded.Latest.CurrentFile is null, "Unrequested path changed progress.");
                progress.OnOutput(Item("/folder/file.txt")); Assert(recorded.Latest.TotalBytes == 200 && recorded.Latest.Bytes == 200, "Selection used full-snapshot totals.");
                progress.OnOutput(Summary(200, plan.Expected.Count)); progress.CompleteSource(); progress.Complete(); recorded.AssertSuccessfulRestore();
            }
            return Task.CompletedTask;
        });
        await tests.Run("Zero-byte files and empty folders use item progress", () =>
        {
            var plan = Plan("empty", 0); var recorded = new ProgressRecorder(); var progress = new RestoreProgress("Project", [plan], recorded);
            progress.Begin(plan); progress.OnOutput(Item("/file.txt"));
            Assert(recorded.Latest.Fraction == .5 && !recorded.Latest.IsIndeterminate && recorded.Latest.TotalBytes == 0, "Zero-byte restore has no usable progress.");
            progress.OnOutput(Item("/")); Assert(recorded.Latest.Fraction == .99 && recorded.Latest.CurrentFile == "empty\\file.txt", "Directory replaced the latest filename.");
            progress.OnOutput(Summary(0, 2)); progress.CompleteSource(); progress.Complete(); recorded.AssertSuccessfulRestore();
            var folder = new RestorePathPlan(Source("folder"), "/folder", null, [new("/folder", "dir", 0)]);
            var empty = new RestoreProgress("Project", [folder], recorded); empty.Begin(folder); empty.OnOutput(Summary(0, 1));
            Assert(recorded.Latest.Fraction == .99 && recorded.Latest.CurrentFile is null, "Empty-directory summary was ignored.");
            return Task.CompletedTask;
        });
        await tests.Run("Fast restore summaries update totals while leaving verification active", () =>
        {
            var plan = Plan("fast", 100); var recorded = new ProgressRecorder(); var progress = new RestoreProgress("Project", [plan], recorded);
            progress.Begin(plan); progress.OnOutput(Summary(100, 2));
            Assert(recorded.Latest.Bytes == 100 && recorded.Latest.Fraction == .99 && recorded.Latest.IsIndeterminate, "Fast restore summary was not processed.");
            Assert(recorded.Items.All(p => p.Fraction < 1), "A summary could claim success after verification fails.");
            return Task.CompletedTask;
        });
        await tests.Run("Restore telemetry tolerates malformed messages and preserves Unicode paths", () =>
        {
            var name = "folder/" + new string('x', 180) + "-résumé-文件[1].txt";
            var source = Source("unicode"); var plan = new RestorePathPlan(source, source.SnapshotPath, null, [new(source.SnapshotPath + "/" + name, "file", 10)]);
            var recorded = new ProgressRecorder(); var progress = new RestoreProgress("Project", [plan], recorded); progress.Begin(plan);
            foreach (var line in new[] { "not json", "{", "null", "[]", "42", "{}", "{\"message_type\":42}",
                "{\"message_type\":\"future\"}", "{\"message_type\":\"verbose_status\",\"action\":null}",
                "{\"message_type\":\"verbose_status\",\"action\":\"restored\",\"item\":42}",
                "{\"message_type\":\"status\",\"bytes_restored\":\"bad\",\"files_restored\":1e99}" }) progress.OnOutput(line);
            Assert(recorded.Latest.Bytes == 0 && recorded.Latest.Fraction == 0, "Malformed telemetry corrupted counts.");
            progress.OnOutput(Item("/" + name)); Assert(recorded.Latest.CurrentFile == "unicode\\" + name.Replace('/', '\\'), "Unicode or long filename changed.");
            var latest = recorded.Latest.CurrentFile;
            progress.OnOutput(Item("/../../escape.txt")); Assert(recorded.Latest.CurrentFile == latest, "Unknown path replaced activity.");
            progress.OnOutput("{\"message_type\":\"status\",\"bytes_restored\":9223372036854775807,\"files_restored\":-1}");
            Assert(recorded.Latest.Bytes == 10 && recorded.Latest.Fraction == .99, "Counters were not bounded by the requested contents.");
            return Task.CompletedTask;
        });
        await tests.Run("Streaming processes retain errors and redaction without capturing stdout", async () =>
        {
            var runner = new ProcessRunner(); var lines = new List<string>();
            string[] args = ["-NoProfile", "-NonInteractive", "-Command", "Write-Output 'fixture-secret'; [Console]::Error.WriteLine('fixture-secret'); exit 7"];
            var result = await runner.RunAsync("powershell.exe", args, onOutput: lines.Add, secret: "fixture-secret", captureOutput: false);
            Assert(result.ExitCode == 7 && result.Output.Length == 0 && result.Error.Contains("[redacted]") && lines.SequenceEqual(["[redacted]"]), "Streaming output or error handling changed.");
            var captured = await runner.RunAsync("powershell.exe", args, secret: "fixture-secret");
            Assert(captured.Output.Contains("[redacted]") && captured.Error.Contains("[redacted]"), "Default output capture changed.");
            using var cancelled = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Throws<OperationCanceledException>(() => runner.RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "Write-Output 'started'; Start-Sleep -Seconds 30"], cancelled.Token,
                onOutput: _ => cancelled.Cancel(), captureOutput: false));
        });
    }

    private static SourceMapping Source(string name) => new(name, "C:\\" + name, "/" + name, name, SourceKind.Project);
    private static RestorePathPlan Plan(string name, long bytes)
    {
        var source = Source(name);
        return new(source, source.SnapshotPath, null, [new(source.SnapshotPath, "dir", 0), new(source.SnapshotPath + "/file.txt", "file", bytes)]);
    }
    public static string Status(long bytes, long files) => JsonSerializer.Serialize(new { message_type = "status", bytes_restored = bytes, files_restored = files });
    public static string Summary(long bytes, long files) => JsonSerializer.Serialize(new { message_type = "summary", bytes_restored = bytes, files_restored = files });
    private static string Item(string path) => JsonSerializer.Serialize(new { message_type = "verbose_status", action = "restored", item = path, size = 0 });
}

internal sealed class ProgressRecorder(Action<BackupProgress>? action = null) : IProgress<BackupProgress>
{
    public List<BackupProgress> Items { get; } = [];
    public BackupProgress Latest => Items[^1];
    public void Report(BackupProgress value) { Items.Add(value); action?.Invoke(value); }
    public void AssertSuccessfulRestore()
    {
        Assert(Items.Count > 0 && Latest.Fraction == 1 && !Latest.IsIndeterminate && Latest.CurrentFile is null, "Restore did not report successful completion.");
        Assert(Items.Take(Items.Count - 1).All(p => p.Fraction < 1), "Restore reported 100% before completion.");
        Assert(Items.Zip(Items.Skip(1)).All(p => p.First.Fraction <= p.Second.Fraction), "Restore progress moved backwards.");
        Assert(Items.Where(p => p.TotalBytes is not null).All(p => p.TotalBytes == Latest.TotalBytes), "Restore totals changed between sources.");
        Assert(Items.Where(p => p.IsIndeterminate).All(p => p.CurrentFile is null), "File activity remained during verification or finalization.");
    }
}
