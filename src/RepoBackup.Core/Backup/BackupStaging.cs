using System.Diagnostics;
using System.Text;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Backup;

public static class BackupStaging
{
    public static async Task WriteFileListAsync(string path, IEnumerable<string> files, CancellationToken token)
    {
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(false), 64 * 1024);
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            await writer.WriteAsync(file.AsMemory(), token);
            await writer.WriteAsync("\0".AsMemory(), token);
        }
    }

    // Delete only our two generated files, never recurse or follow a reparse point.
    public static string? Clean(AppPaths paths, string jobId)
    {
        try
        {
            if (!Guid.TryParseExact(jobId, "N", out _)) return "Skipped staging cleanup: unknown job identity.";
            var folder = Path.GetFullPath(Path.Combine(paths.JobsDirectory, jobId));
            if (!PathSafety.IsWithin(folder, paths.JobsDirectory)) return "Skipped staging cleanup: unsafe path.";
            if (!Directory.Exists(folder)) return null;
            for (var current = folder; current is not null; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return "Skipped staging cleanup: linked path.";
            foreach (var name in new[] { "files.raw", "manifest.json" })
            {
                var file = Path.Combine(folder, name);
                if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return "Skipped staging cleanup: linked file.";
            }
            foreach (var name in new[] { "files.raw", "manifest.json" }) File.Delete(Path.Combine(folder, name));
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return "Temporary staging cleanup deferred: " + e.Message; }
    }

    public static void Sweep(AppPaths paths, IEnumerable<JobRecord> jobs, Action<JobRecord> save)
    {
        foreach (var job in jobs.Where(j => j.FinishedAt is not null))
        {
            // Most jobs have already removed their staging. Avoid probing historical
            // owner processes (and throwing for exited PIDs) when there is nothing to clean.
            if (!Guid.TryParseExact(job.Id, "N", out _) || !Directory.Exists(Path.Combine(paths.JobsDirectory, job.Id))) continue;
            try
            {
                using var owner = Process.GetProcessById(job.OwnerProcessId);
                if (!owner.HasExited && owner.StartTime.ToUniversalTime().Ticks == job.OwnerStartTicks) continue;
            }
            catch (System.ComponentModel.Win32Exception) { continue; } // Cannot prove the owner is inactive.
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { }
            if (Clean(paths, job.Id) is { } warning && !job.Messages.Contains(warning))
                save(job with { Messages = [.. job.Messages, warning] });
        }
    }
}
