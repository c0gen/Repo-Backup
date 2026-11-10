using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Recovery;

internal sealed class GitArchivePaths(List<(string Original, string Restored)> entries)
{
    public static async Task<GitArchivePaths> ReadAsync(string target, RecoveryManifest manifest, CancellationToken token)
    {
        var entries = new List<(string, string)>();
        var root = Path.Combine(target, "git-registration-archive");
        if (!Directory.Exists(root)) return new(entries);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Git archive root is a link.");
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            token.ThrowIfCancellationRequested();
            var marker = Path.Combine(directory, "RepoBackup-registration.json");
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0 || new FileInfo(marker).Length > 256 * 1024)
                throw new InvalidDataException("Unsafe Git archive metadata.");
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(marker, token));
            var original = json.RootElement.GetProperty("originalAdmin").GetString() ?? throw new InvalidDataException("Missing Git archive identity.");
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original)))[..16].ToLowerInvariant();
            if (!Path.GetFileName(directory).Equals(key + "-" + PathSafety.SafeName(Path.GetFileName(original)), StringComparison.Ordinal)
                || !manifest.Sources.Any(s => PathSafety.IsWithin(original, s.OriginalPath))
                || !string.Equals(Path.GetFileName(Path.GetDirectoryName(original)), "worktrees", StringComparison.OrdinalIgnoreCase)
                || manifest.SchemaVersion >= 4 && !manifest.GitRegistrations.Any(r => !r.Available && r.AdministrativeDirectory.Equals(original, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Git archive identity does not match the captured registration: " + original);
            entries.Add((original, directory));
        }
        return new(entries);
    }

    public string TargetPath(RestorePathPlan plan, SnapshotFile node, string target)
    {
        var original = node.Path == plan.Source.SnapshotPath ? plan.Source.OriginalPath
            : Path.Combine(plan.Source.OriginalPath, node.Path[(plan.Source.SnapshotPath.Length + 1)..].Replace('/', Path.DirectorySeparatorChar));
        var entry = entries.Where(e => PathSafety.IsWithin(original, e.Original)).OrderByDescending(e => e.Original.Length).FirstOrDefault();
        var path = entry.Original is null ? plan.TargetPath(target, node)
            : Path.Combine(entry.Restored, Path.GetRelativePath(entry.Original, original));
        if (!PathSafety.IsWithin(PathSafety.PhysicalPath(path), target)) throw new InvalidDataException("Git archive path escapes recovery output.");
        return path;
    }
}
