using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Git;

public sealed partial class GitInspector
{
    public async Task<GitInventory> InventoryAsync(IEnumerable<string> roots, CancellationToken token = default)
    {
        var inventory = new GitInventory();
        var pending = new Queue<(string Path, string? Common, SourceKind Kind)>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(r => Directory.Exists(Path.Combine(r, ".git")))) pending.Enqueue((root, null, SourceKind.Project));
        var registeredCommon = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inspected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var commonSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Error(string code, string path, Exception error) => inventory.Issues.Add(new(code, BackupIssueSeverity.CoverageError, path, error.Message));
        while (pending.TryDequeue(out var next))
        {
            token.ThrowIfCancellationRequested();
            if (!inspected.Add(PathSafety.Normalize(next.Path))) continue;
            if (!Directory.Exists(Path.Combine(next.Path, ".git")) && !File.Exists(Path.Combine(next.Path, ".git")))
            {
                if (next.Kind == SourceKind.Worktree && next.Common is not null)
                    Error("git.metadata-unavailable", next.Path, new IOException("Available registered worktree has no accessible .git metadata: " + next.Path));
                continue;
            }
            GitContext context;
            try { context = await ContextAsync(next.Path, next.Common ?? registeredCommon.GetValueOrDefault(PathSafety.Normalize(next.Path)), token); }
            catch (Exception e) when (IsInventoryError(e)) { inspected.Remove(PathSafety.Normalize(next.Path)); Error("git.metadata-unavailable", next.Path, e); continue; }
            inventory.Issues.RemoveAll(i => i.Code == "git.metadata-unavailable" && string.Equals(i.Path, next.Path, StringComparison.OrdinalIgnoreCase));
            inventory.Contexts.Add(context);
            if (next.Kind != SourceKind.Project) inventory.Sources.Add((context.WorkingPath, next.Kind));
            inventory.Sources.Add((context.GitDirectory, SourceKind.GitMetadata));
            inventory.Sources.Add((context.CommonDirectory, SourceKind.GitMetadata));
            if (!context.GitDirectory.Equals(context.CommonDirectory, StringComparison.OrdinalIgnoreCase))
                inventory.Registrations.Add(new(context.CommonDirectory, context.GitDirectory, context.WorkingPath, true, null));
            if (commonSeen.Add(context.CommonDirectory))
            {
                try
                {
                    var output = await ReadAsync(context, ["worktree", "list", "--porcelain", "-z"], token);
                    output.EnsureSuccess("Discover Git worktrees");
                    foreach (var record in ParseWorktrees(output.Output))
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            var foreign = record.Path.StartsWith('/') && !record.Path.StartsWith("//", StringComparison.Ordinal);
                            if (foreign)
                            {
                                RegisterUnavailable(record, !record.Locked, "Worktree belongs to another operating system"); continue;
                            }
                            var path = PathSafety.Normalize(record.Path);
                            // Git can list a submodule's administrative database as its main entry.
                            // It is already captured as metadata, not an additional working folder.
                            if (path.Equals(context.CommonDirectory, StringComparison.OrdinalIgnoreCase)) continue;
                            try { _ = File.GetAttributes(path); }
                            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
                            {
                                RegisterUnavailable(record, !record.Locked && record.PrunableReason is not null && VolumeAvailable(path), "Registered worktree folder is missing"); continue;
                            }
                            inventory.Sources.Add((path, SourceKind.Worktree));
                            registeredCommon[path] = context.CommonDirectory;
                            pending.Enqueue((path, context.CommonDirectory, SourceKind.Worktree));
                        }
                        catch (Exception e) when (IsInventoryError(e)) { Error("git.worktree-unavailable", record.Path, e); }
                    }
                }
                catch (Exception e) when (IsInventoryError(e)) { Error("git.worktree-discovery", context.WorkingPath, e); }
            }
            try
            {
                var lfs = await ReadAsync(context, ["config", "--path", "--get", "lfs.storage"], token);
                if (lfs.ExitCode is not (0 or 1)) lfs.EnsureSuccess("Read Git LFS storage");
                if (lfs.ExitCode == 0 && !string.IsNullOrWhiteSpace(lfs.Output))
                {
                    var value = lfs.Output.Trim();
                    if (value.StartsWith('/') && !value.StartsWith("//", StringComparison.Ordinal)) throw new InvalidDataException("Git LFS storage uses a foreign path: " + value);
                    var storage = PathSafety.Normalize(Path.GetFullPath(value, context.GitDirectory));
                    if (!Directory.Exists(storage)) throw new IOException("Git LFS storage unavailable: " + storage);
                    inventory.Sources.Add((storage, SourceKind.GitMetadata));
                }
            }
            catch (Exception e) when (IsInventoryError(e)) { Error("git.lfs-unavailable", context.WorkingPath, e); }
            try
            {
                var entries = await ReadAsync(context, ["ls-files", "--stage", "-z"], token);
                entries.EnsureSuccess("Read tracked files and submodules");
                foreach (var entry in entries.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                {
                    var tab = entry.IndexOf('\t'); if (tab < 0) continue;
                    var path = Path.GetFullPath(entry[(tab + 1)..], context.WorkingPath);
                    inventory.TrackedFiles.Add(path);
                    if (!entry.StartsWith("160000 ", StringComparison.Ordinal)) continue;
                    try
                    {
                        if (File.Exists(Path.Combine(path, ".git")) || Directory.Exists(Path.Combine(path, ".git"))) pending.Enqueue((path, null, SourceKind.Worktree));
                        else if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
                            throw new IOException("Local submodule contents have no accessible Git database: " + path);
                    }
                    catch (Exception e) when (IsInventoryError(e)) { Error("git.submodule-unavailable", path, e); }
                }
            }
            catch (Exception e) when (IsInventoryError(e)) { Error("git.tracked-files-unavailable", context.WorkingPath, e); }

            void RegisterUnavailable(GitWorktreeRecord record, bool advisory, string reason)
            {
                var admins = Path.Combine(context.CommonDirectory, "worktrees");
                if (Directory.Exists(admins)) foreach (var admin in Directory.EnumerateDirectories(admins))
                {
                    var pointer = Path.Combine(admin, "gitdir");
                    if (File.Exists(pointer) && File.ReadAllText(pointer).Trim().Replace('\\', '/').Equals(record.Path.TrimEnd('/') + "/.git", StringComparison.OrdinalIgnoreCase))
                        inventory.Registrations.Add(new(context.CommonDirectory, PathSafety.Normalize(admin), record.Path, false, reason));
                }
                inventory.Issues.Add(new(advisory ? "git.stale-worktree" : "git.worktree-unavailable",
                    advisory ? BackupIssueSeverity.Advisory : BackupIssueSeverity.CoverageError, record.Path,
                    reason + ": " + record.Path + (record.Locked ? " (locked)" : "") + ". Available Git history is included."));
            }
        }
        var databases = new Queue<string>(inventory.Sources.Where(s => s.Kind == SourceKind.GitMetadata).Select(s => s.Path));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (databases.TryDequeue(out var database))
        {
            token.ThrowIfCancellationRequested(); if (!seen.Add(database)) continue;
            try
            {
                var alternate = Path.Combine(database, "objects", "info", "alternates");
                if (!File.Exists(alternate)) continue;
                foreach (var line in await File.ReadAllLinesAsync(alternate, token))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        if (line.StartsWith('/') && !line.StartsWith("//", StringComparison.Ordinal)) throw new IOException("Git object alternate uses a foreign path: " + line);
                        var objects = Path.GetFullPath(line, Path.Combine(database, "objects"));
                        if ((File.GetAttributes(objects) & FileAttributes.Directory) == 0) throw new IOException("Unresolved Git object alternate: " + objects);
                        var parent = Directory.GetParent(objects)?.FullName ?? objects;
                        inventory.Sources.Add((parent, SourceKind.GitMetadata)); databases.Enqueue(parent);
                    }
                    catch (Exception e) when (IsInventoryError(e)) { Error("git.alternate-unavailable", line, e); }
                }
            }
            catch (Exception e) when (IsInventoryError(e)) { Error("git.alternate-unavailable", database, e); }
        }
        var distinct = inventory.Sources.DistinctBy(s => s.Path, StringComparer.OrdinalIgnoreCase).ToList();
        inventory.Sources.Clear(); inventory.Sources.AddRange(distinct);
        return inventory;
    }
}
