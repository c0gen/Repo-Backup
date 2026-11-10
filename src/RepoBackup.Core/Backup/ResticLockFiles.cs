using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Backup;

internal static class ResticLockFiles
{
    public static async Task RemoveExitedOwnersAsync(Destination destination,
        Func<IEnumerable<string>, Task<ProcessResult>> read, CancellationToken token)
    {
        // Windows can retain an exited process object while another program holds a handle.
        // Restic checks only whether OpenProcess succeeds. Restrict this fallback to our own
        // Windows account and host, and require positive evidence that the owner has exited.
        if (destination.RepositoryId is null) return;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var account = identity.Name;
            var config = await read(["cat", "config"]);
            if (config.ExitCode != 0) return;
            using (var document = JsonDocument.Parse(config.Output))
                if (!document.RootElement.TryGetProperty("id", out var id) || id.GetString() != destination.RepositoryId) return;
            var listing = await read(["list", "locks"]);
            if (listing.ExitCode != 0) return;
            // `list locks` emits one content ID per line, including with --json in 0.19.1.
            var ids = listing.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(id => id.Trim());
            var repository = PathSafety.PhysicalPath(destination.Path);
            var directory = Path.Combine(destination.Path, "locks");
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
            foreach (var id in ids)
            {
                token.ThrowIfCancellationRequested();
                if (id is null || id.Length != 64 || !id.All(Uri.IsHexDigit)) continue;
                var metadata = await read(["cat", "lock", id]);
                if (metadata.ExitCode != 0) continue;
                using var document = JsonDocument.Parse(metadata.Output);
                var row = document.RootElement;
                if (!row.TryGetProperty("hostname", out var host) || !string.Equals(host.GetString(), Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                    || !row.TryGetProperty("username", out var user) || !string.Equals(user.GetString(), account, StringComparison.OrdinalIgnoreCase)
                    || !row.TryGetProperty("time", out var time) || !time.TryGetDateTimeOffset(out _)
                    || !row.TryGetProperty("pid", out var pid) || !pid.TryGetInt32(out var owner) || owner <= 0 || !Exited(owner)) continue;
                var file = Path.Combine(directory, id);
                if (!PathSafety.IsWithin(PathSafety.PhysicalPath(file), repository)
                    || (File.GetAttributes(file) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) continue;
                // Lock files are immutable, content-addressed blobs. Never remove a different
                // file which appeared at this name between decrypting the metadata and deletion.
                await using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    if (!Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).Equals(id, StringComparison.OrdinalIgnoreCase)) continue;
                token.ThrowIfCancellationRequested();
                File.Delete(file);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or System.Security.SecurityException or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        { /* An unavailable or ambiguous lock remains intact; return the native lock error. */ }
    }

    private static bool Exited(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
}
