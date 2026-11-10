using System.Security.Cryptography;
using System.Text;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Recovery;

internal static class GitTopologyRecovery
{
    public static async Task<List<GitContext>> PrepareAsync(string target, IReadOnlyList<SourceMapping> sources,
        IReadOnlyList<string> metadata, IReadOnlyList<GitContext> contexts, IReadOnlyList<GitRegistration> registrations, CancellationToken token)
    {
        string Restored(SourceMapping source) => Path.Combine(target, "sources", source.RestoreFolder);
        string? Map(string original)
        {
            if (!Path.IsPathFullyQualified(original)) return null;
            var source = sources.Where(s => PathSafety.IsWithin(original, s.OriginalPath)).OrderByDescending(s => s.OriginalPath.Length).FirstOrDefault();
            return source is null ? null : Path.Combine(Restored(source), Path.GetRelativePath(PathSafety.Normalize(source.OriginalPath), PathSafety.Normalize(original)));
        }
        string Original(string restored)
        {
            var source = sources.Where(s => PathSafety.IsWithin(restored, Restored(s))).OrderByDescending(s => s.RestoreFolder.Length).First();
            return PathSafety.Normalize(Path.Combine(source.OriginalPath, Path.GetRelativePath(Restored(source), restored)));
        }
        var databases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var original in metadata.Concat(contexts.Select(c => c.CommonDirectory)).Concat(sources.Where(s => s.Kind == SourceKind.GitMetadata).Select(s => s.OriginalPath)))
            if (Map(original) is { } path && Directory.Exists(path)) databases.Add(path);
        if (contexts.Count == 0)
        {
            // Legacy manifests have no relationship graph. Discover only captured directories, never live originals.
            var pending = new Stack<string>(sources.Select(Restored).Where(Directory.Exists));
            while (pending.TryPop(out var directory))
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(Path.Combine(directory, "HEAD")) && Directory.Exists(Path.Combine(directory, "worktrees"))) databases.Add(directory);
                foreach (var child in Directory.EnumerateDirectories(directory))
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
            }
        }
        var result = contexts.ToList();
        var links = new List<(string Admin, string Database, string OriginalAdmin, string Worktree)>();
        foreach (var database in databases)
        {
            token.ThrowIfCancellationRequested();
            var worktrees = Path.Combine(database, "worktrees");
            if (!Directory.Exists(worktrees)) continue;
            if ((File.GetAttributes(worktrees) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Restored worktree metadata is a link.");
            foreach (var admin in Directory.EnumerateDirectories(worktrees))
            {
                token.ThrowIfCancellationRequested();
                if ((File.GetAttributes(admin) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Restored worktree registration is a link.");
                var originalAdmin = Original(admin);
                var registration = registrations.SingleOrDefault(r => r.AdministrativeDirectory.Equals(originalAdmin, StringComparison.OrdinalIgnoreCase));
                var pointerFile = Path.Combine(admin, "gitdir");
                if (!File.Exists(pointerFile)) throw new InvalidDataException("Captured worktree registration has no gitdir: " + originalAdmin);
                var pointer = (await File.ReadAllTextAsync(pointerFile, token)).Trim();
                var originalWorking = registration?.WorktreePath ?? (Path.IsPathFullyQualified(pointer) ? Path.GetDirectoryName(pointer) : null);
                var working = originalWorking is null ? null : Map(originalWorking);
                if (registration?.Available != false && working is not null && File.Exists(Path.Combine(working, ".git")))
                {
                    if (contexts.Count > 0 && registration is null && !contexts.Any(c => c.WorkingPath.Equals(originalWorking, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("Captured worktree has no resolved Git relationship: " + originalWorking);
                    links.Add((admin, database, originalAdmin, originalWorking!));
                }
                else
                    await ArchiveAsync(target, database, admin, originalAdmin, pointer, token);
            }
        }
        if (links.GroupBy(l => PathSafety.Normalize(l.Worktree), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new InvalidDataException("Captured worktree has ambiguous administrative registrations.");
        foreach (var link in links)
        {
            var working = Map(link.Worktree)!;
            var pointerFile = Path.Combine(working, ".git");
            var contents = (await File.ReadAllTextAsync(pointerFile, token)).Trim();
            if (!contents.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid captured worktree pointer: " + link.Worktree);
            var originalPointer = contents[7..].Trim();
            if (!Path.IsPathFullyQualified(originalPointer)) originalPointer = Path.GetFullPath(originalPointer, link.Worktree);
            // Relocation is accepted only when both captured sides identify the same administrative record.
            if (!PathSafety.Normalize(originalPointer).Equals(link.OriginalAdmin, StringComparison.OrdinalIgnoreCase)
                && (!string.Equals(Path.GetFileName(originalPointer), Path.GetFileName(link.OriginalAdmin), StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(Path.GetFileName(Path.GetDirectoryName(originalPointer)), "worktrees", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Captured worktree pointer does not match its registration: " + link.Worktree);
            var commonFile = Path.Combine(link.Admin, "commondir");
            var commonValue = (await File.ReadAllTextAsync(commonFile, token)).Trim();
            var originalCommon = PathSafety.Normalize(Path.GetFullPath(commonValue, link.OriginalAdmin));
            if (!originalCommon.Equals(Original(link.Database), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Captured worktree has an inconsistent common directory: " + link.Worktree);
            await WriteAsync(target, pointerFile, "gitdir: " + link.Admin.Replace('\\', '/') + "\n", token);
            await WriteAsync(target, Path.Combine(link.Admin, "gitdir"), Path.Combine(working, ".git").Replace('\\', '/') + "\n", token);
            await WriteAsync(target, commonFile, link.Database.Replace('\\', '/') + "\n", token);
            if (!result.Any(c => c.WorkingPath.Equals(link.Worktree, StringComparison.OrdinalIgnoreCase))) result.Add(new(link.Worktree, link.OriginalAdmin, Original(link.Database)));
        }
        return result;
    }

    private static async Task ArchiveAsync(string target, string database, string admin, string originalAdmin, string pointer, CancellationToken token)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(originalAdmin)))[..16].ToLowerInvariant();
        var archive = Path.Combine(target, "git-registration-archive", key + "-" + PathSafety.SafeName(Path.GetFileName(admin)));
        if (!PathSafety.IsWithin(PathSafety.PhysicalPath(admin), target) || !PathSafety.IsWithin(PathSafety.PhysicalPath(archive), target))
            throw new InvalidDataException("Git registration archive would escape the restore directory.");
        var headFile = Path.Combine(admin, "HEAD");
        if (File.Exists(headFile))
        {
            var head = (await File.ReadAllTextAsync(headFile, token)).Trim();
            if (head.Length is 40 or 64 && head.All(Uri.IsHexDigit))
            {
                var reference = Path.Combine(database, "refs", "repobackup", "orphan-worktrees", key);
                ManifestPaths.CreateDirectory(Path.GetDirectoryName(reference)!, target);
                await using var stream = new FileStream(reference, FileMode.CreateNew, FileAccess.Write);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head + "\n"), token);
            }
        }
        ManifestPaths.CreateDirectory(Path.GetDirectoryName(archive)!, target);
        Directory.Move(admin, archive);
        await File.WriteAllTextAsync(Path.Combine(archive, "RepoBackup-registration.json"), Json.Write(new { originalAdmin, originalPointer = pointer,
            reason = "Worktree contents were unavailable in this snapshot. Administrative metadata is preserved here." }), token);
    }

    private static async Task WriteAsync(string target, string path, string value, CancellationToken token)
    {
        if (!PathSafety.IsWithin(PathSafety.PhysicalPath(path), target) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Unsafe restored Git metadata path.");
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Write); stream.SetLength(0);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(value), token);
    }
}
