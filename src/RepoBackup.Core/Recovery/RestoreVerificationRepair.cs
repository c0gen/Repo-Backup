using System.Text;
using System.Text.Json;
using RepoBackup.Core.Backup;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Recovery;

public sealed record RestoreFileRepair(string Path, string Reason);

public static class RestoreVerificationRepair
{
    // Only pinned restic's completed readback mismatches qualify. Transfer, access,
    // repository and unknown failures retain their normal failure behavior.
    public static async Task<List<RestoreFileRepair>> RepairAsync(ResticClient restic, Destination destination,
        SnapshotSummary snapshot, RestorePathPlan plan, string target, ProcessResult result,
        Action<string>? phase = null, CancellationToken token = default)
    {
        if (result.ExitCode == 0) return [];
        var failures = ReadFailures(result);
        if (failures is null) { result.EnsureSuccess("Restore requested contents"); return []; }
        var expected = plan.Expected.Where(n => n.Type == "file").ToDictionary(
            n => PathSafety.Normalize(plan.TargetPath(target, n)), StringComparer.OrdinalIgnoreCase);
        var selected = new List<(SnapshotFile Node, RestoreFileRepair Repair)>();
        foreach (var failure in failures)
        {
            string path;
            try { path = PathSafety.Normalize(failure.Path); }
            catch (ArgumentException) { result.EnsureSuccess("Restore requested contents"); throw; }
            if (!expected.TryGetValue(path, out var node) || !PathSafety.IsWithin(path, target))
                throw new IOException("Verification reported a file outside the requested recovery contents: " + failure.Path);
            ManifestPaths.ValidateRelative(node.Path[plan.Tree.Length..].TrimStart('/'));
            ValidateTarget(path, target);
            selected.Add((node, failure with { Path = path }));
        }

        var temporary = Path.Combine(PathSafety.Normalize(target), ".verification-repair-" + Guid.NewGuid().ToString("N"));
        var filters = temporary + ".include";
        ManifestPaths.CreateDirectory(temporary, target);
        try
        {
            await File.WriteAllLinesAsync(filters, selected.Select(s => RestorePaths.LiteralFilter(s.Node.Path[plan.Tree.Length..])),
                new UTF8Encoding(false), token);
            phase?.Invoke("Repairing files that failed recovery verification");
            // Restore to a private, new tree and verify before atomic replacement.
            // This cannot write through a hard link from the output to an original file.
            var repaired = await restic.RunAsync(destination, ["restore", snapshot.Id + ":" + plan.Tree,
                "--target", temporary, "--include-file", filters, "--verify", "--overwrite", "never"], token, captureOutput: false);
            repaired.EnsureSuccess("Verify repaired recovery contents for " + string.Join("; ", selected.Take(5).Select(s => s.Repair.Path)) +
                "; keep the recovery folder closed while restoration runs");
            foreach (var (node, repair) in selected)
            {
                token.ThrowIfCancellationRequested();
                var file = Path.Combine(temporary, node.Path[plan.Tree.Length..].TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                ValidateTarget(file, target);
                if (new FileInfo(file).Length != node.Size) throw new IOException("Repaired recovery file has an unexpected size: " + node.Path);
                ValidateTarget(repair.Path, target);
                File.Move(file, repair.Path, overwrite: true);
            }
            return selected.Select(s => s.Repair).ToList();
        }
        finally
        {
            try { File.Delete(filters); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            // The absolute private root is constructed inside this recovery directory.
            // Directory.Delete does not follow directory reparse points.
            if (PathSafety.IsWithin(temporary, target))
                try { Directory.Delete(temporary, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void ValidateTarget(string path, string target)
    {
        ManifestPaths.CreateDirectory(Path.GetDirectoryName(path)!, target);
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException("Unsafe recovery verification target: " + path);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static List<RestoreFileRepair>? ReadFailures(ProcessResult result)
    {
        if (result.ExitCode != 1) return null;
        var failures = new List<RestoreFileRepair>(); var exit = false;
        try
        {
            foreach (var line in result.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                using var json = JsonDocument.Parse(line); var item = json.RootElement;
                var type = item.GetProperty("message_type").GetString();
                if (type == "exit_error")
                {
                    if (exit || item.GetProperty("code").GetInt32() != 1 ||
                        item.GetProperty("message").GetString() != $"Fatal: There were {failures.Count} errors") return null;
                    exit = true; continue;
                }
                if (exit || type != "error" || item.GetProperty("during").GetString() != "restore") return null;
                var message = item.GetProperty("error").GetProperty("message").GetString();
                if (message is null || !message.StartsWith("Invalid file size for ", StringComparison.Ordinal) &&
                    !message.StartsWith("Unexpected content in ", StringComparison.Ordinal)) return null;
                var path = item.GetProperty("item").GetString();
                if (string.IsNullOrEmpty(path)) return null;
                failures.Add(new(path, message));
                if (failures.Count > 100) return null;
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException) { return null; }
        return exit && failures.Count > 0 && failures.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == failures.Count ? failures : null;
    }
}
