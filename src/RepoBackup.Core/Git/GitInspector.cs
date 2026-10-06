using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Git;

public sealed class GitInspector(ProcessRunner processes)
{
    private static readonly Dictionary<string, string> ReadEnvironment = new() { ["GIT_OPTIONAL_LOCKS"] = "0", ["GIT_TERMINAL_PROMPT"] = "0" };
    public Task<ProcessResult> ReadAsync(string root, IEnumerable<string> args, CancellationToken token = default) =>
        processes.RunAsync("git", new[] { "--no-optional-locks", "-c", "core.fsmonitor=false", "-C", root }.Concat(args), token, environment: ReadEnvironment);

    public Task<List<(string Path, SourceKind Kind)>> AssociatedSourcesAsync(string root, List<string> warnings, CancellationToken token) =>
        AssociatedSourcesAsync(root, warnings, token, new(StringComparer.OrdinalIgnoreCase));

    private async Task<List<(string Path, SourceKind Kind)>> AssociatedSourcesAsync(string root, List<string> warnings, CancellationToken token, HashSet<string> inspected)
    {
        var result = new List<(string, SourceKind)>();
        if (!Directory.Exists(Path.Combine(root, ".git")) && !File.Exists(Path.Combine(root, ".git"))) return result;
        try
        {
            if (!inspected.Add(PathSafety.PhysicalPath(root))) return result;
            var dirs = await ReadAsync(root, ["rev-parse", "--path-format=absolute", "--git-dir", "--git-common-dir"], token);
            dirs.EnsureSuccess("Read Git metadata");
            foreach (var directory in dirs.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var normalized = PathSafety.Normalize(directory);
                if (!Directory.Exists(normalized)) warnings.Add("Unresolved Git database: " + normalized);
                else result.Add((normalized, SourceKind.GitMetadata));
            }
            var worktrees = await ReadAsync(root, ["worktree", "list", "--porcelain", "-z"], token);
            worktrees.EnsureSuccess("Discover Git worktrees");
            foreach (var field in worktrees.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                if (field.StartsWith("worktree ", StringComparison.Ordinal))
                {
                    var path = PathSafety.Normalize(field[9..]);
                    if (!Directory.Exists(path)) warnings.Add("Worktree unavailable: " + path);
                    else result.Add((path, SourceKind.Worktree));
                }
            var lfs = await ReadAsync(root, ["config", "--path", "--get", "lfs.storage"], token);
            if (lfs.ExitCode == 0 && !string.IsNullOrWhiteSpace(lfs.Output))
            {
                var gitDirectory = dirs.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
                var storage = Path.GetFullPath(lfs.Output.Trim(), gitDirectory);
                if (Directory.Exists(storage)) result.Add((storage, SourceKind.GitMetadata));
                else warnings.Add("Git LFS storage unavailable: " + storage);
            }
            // Object alternates are real dependencies, including external databases. Capture the entire chain.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<string>(result.Where(r => r.Item2 == SourceKind.GitMetadata).Select(r => r.Item1));
            while (pending.TryDequeue(out var directory))
            {
                if (!seen.Add(directory)) continue;
                var alternates = Path.Combine(directory, "objects", "info", "alternates");
                if (!File.Exists(alternates)) continue;
                foreach (var line in await File.ReadAllLinesAsync(alternates, token))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var objects = Path.GetFullPath(line, Path.Combine(directory, "objects"));
                    if (!Directory.Exists(objects)) { warnings.Add("Unresolved Git object alternate: " + objects); continue; }
                    // Include the complete parent database so alternates can be reconstructed.
                    var database = Directory.GetParent(objects)?.FullName ?? objects;
                    result.Add((database, SourceKind.GitMetadata)); pending.Enqueue(database);
                }
            }
            // A submodule can use a separate Git database outside .git/modules. Inspect initialized modules in each worktree.
            var workingRoots = result.Where(r => r.Item2 == SourceKind.Worktree).Select(r => r.Item1).Prepend(root).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var workingRoot in workingRoots)
            {
                var entries = await ReadAsync(workingRoot, ["ls-files", "--stage", "-z"], token); entries.EnsureSuccess("Discover local submodule databases");
                foreach (var entry in entries.Output.TrimEnd('\r', '\n').Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(e => e.StartsWith("160000 ", StringComparison.Ordinal)))
                {
                    var tab = entry.IndexOf('\t'); if (tab < 0) continue;
                    var module = Path.GetFullPath(entry[(tab + 1)..], workingRoot);
                    if (File.Exists(Path.Combine(module, ".git")) || Directory.Exists(Path.Combine(module, ".git")))
                        result.AddRange(await AssociatedSourcesAsync(module, warnings, token, inspected));
                    else if (Directory.Exists(module) && Directory.EnumerateFileSystemEntries(module).Any())
                        warnings.Add("Local submodule contents have no accessible Git database: " + module);
                }
            }
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { warnings.Add("Git recovery information incomplete: " + e.Message); }
        return result.DistinctBy(r => r.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<HashSet<string>> TrackedFilesAsync(string root, CancellationToken token)
    {
        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(Path.Combine(root, ".git")) && !File.Exists(Path.Combine(root, ".git"))) return tracked;
        var result = await ReadAsync(root, ["ls-files", "--cached", "--recurse-submodules", "-z"], token);
        result.EnsureSuccess("Read tracked Git files");
        foreach (var path in result.Output.TrimEnd('\r', '\n').Split('\0', StringSplitOptions.RemoveEmptyEntries)) tracked.Add(Path.GetFullPath(path, root));
        return tracked;
    }
}
