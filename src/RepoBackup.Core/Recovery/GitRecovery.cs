using System.Text.RegularExpressions;
using RepoBackup.Core.Git;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Recovery;

public sealed partial class GitRecovery(GitInspector git)
{
    [GeneratedRegex(@"^(\s*(?:worktree|storage|path)\s*=\s*)(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ConfigPathLine();

    public async Task RepairAsync(string recoveryDirectory, IReadOnlyList<SourceMapping> sources, CancellationToken token, IReadOnlyList<string>? metadataDirectories = null,
        IReadOnlyList<GitContext>? contexts = null, IReadOnlyList<GitRegistration>? registrations = null)
    {
        metadataDirectories ??= [];
        bool IsMetadata(string path, SourceMapping source) => source.Kind == SourceKind.GitMetadata ||
            path.Replace('\\', '/').Contains("/.git/", StringComparison.OrdinalIgnoreCase) ||
            metadataDirectories.Any(m => PathSafety.IsWithin(path, m));
        async Task WriteMetadataAsync(string file, string contents)
        {
            if (!PathSafety.IsWithin(PathSafety.PhysicalPath(file), recoveryDirectory)) throw new InvalidDataException("Restored Git metadata would escape the recovery directory.");
            // Git for Windows hides .git pointer files. Create/overwrite APIs fail on hidden Windows files; open the restored file explicitly.
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.None);
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            await writer.WriteAsync(contents.AsMemory(), token);
        }
        string Restored(SourceMapping source) => Path.Combine(recoveryDirectory, "sources", source.RestoreFolder);
        string Map(string original)
        {
            original = PathSafety.Normalize(original);
            if (PathSafety.IsWithin(original, recoveryDirectory) && sources.Any(s => PathSafety.IsWithin(original, Restored(s)))) return original;
            var source = sources.Where(s => PathSafety.IsWithin(original, s.OriginalPath)).OrderByDescending(s => s.OriginalPath.Length).FirstOrDefault()
                ?? throw new InvalidDataException("A Git reference has an unresolved external dependency: " + original);
            var mapped = Path.GetFullPath(Path.Combine(Restored(source), Path.GetRelativePath(PathSafety.Normalize(source.OriginalPath), original)));
            if (!PathSafety.IsWithin(mapped, recoveryDirectory)) throw new InvalidDataException("Git reference would escape the recovery directory.");
            return mapped;
        }
        string Original(string restored)
        {
            var source = sources.Where(s => PathSafety.IsWithin(restored, Restored(s))).OrderByDescending(s => Restored(s).Length).First();
            return PathSafety.Normalize(Path.Combine(source.OriginalPath, Path.GetRelativePath(Restored(source), restored)));
        }
        var resolvedContexts = await GitTopologyRecovery.PrepareAsync(recoveryDirectory, sources, metadataDirectories, contexts ?? [], registrations ?? [], token);
        var workingRoots = new List<string>();
        foreach (var source in sources)
        {
            var restored = Restored(source); if (!Directory.Exists(restored)) continue;
            var pending = new Stack<string>(); pending.Push(restored);
            while (pending.TryPop(out var directory))
            {
                token.ThrowIfCancellationRequested();
                foreach (var child in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (Path.GetFileName(child).Equals(".git", StringComparison.OrdinalIgnoreCase)) workingRoots.Add(directory);
                        pending.Push(child); continue;
                    }
                    var name = Path.GetFileName(child);
                    var original = Original(child); var originalDirectory = Path.GetDirectoryName(original)!;
                    if (name == ".git")
                    {
                        var contents = (await File.ReadAllTextAsync(child, token)).Trim();
                        if (!contents.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid restored .git pointer.");
                        var pointer = Path.GetFullPath(contents[7..].Trim(), originalDirectory);
                        await WriteMetadataAsync(child, "gitdir: " + Map(pointer).Replace('\\', '/') + "\n"); workingRoots.Add(directory);
                    }
                    else if (name is "gitdir" or "commondir" && IsMetadata(original, source))
                    {
                        var pointer = Path.GetFullPath((await File.ReadAllTextAsync(child, token)).Trim(), originalDirectory);
                        await WriteMetadataAsync(child, Map(pointer).Replace('\\', '/') + "\n");
                    }
                    else if (name == "alternates" && original.Replace('\\', '/').EndsWith("/objects/info/alternates", StringComparison.OrdinalIgnoreCase))
                    {
                        var lines = await File.ReadAllLinesAsync(child, token);
                        await WriteMetadataAsync(child, string.Join("\n", lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => Map(Path.GetFullPath(l, Path.GetFullPath("..", originalDirectory))).Replace('\\', '/'))) + "\n");
                    }
                    else if (name is "config" or "config.worktree" && IsMetadata(original, source))
                    {
                        var lines = await File.ReadAllLinesAsync(child, token); var section = "";
                        for (var i = 0; i < lines.Length; i++)
                        {
                            if (lines[i].TrimStart().StartsWith('[')) section = lines[i].Trim();
                            var match = ConfigPathLine().Match(lines[i]); if (!match.Success) continue;
                            var key = match.Groups[1].Value.Trim().Split('=')[0].Trim();
                            if (!((section.Equals("[core]", StringComparison.OrdinalIgnoreCase) && key.Equals("worktree", StringComparison.OrdinalIgnoreCase)) || (section.Equals("[lfs]", StringComparison.OrdinalIgnoreCase) && key.Equals("storage", StringComparison.OrdinalIgnoreCase)) || (section.StartsWith("[include", StringComparison.OrdinalIgnoreCase) && key.Equals("path", StringComparison.OrdinalIgnoreCase)))) continue;
                            var value = match.Groups[2].Value.Trim().Trim('"');
                            var path = Path.GetFullPath(value.Replace("\\\\", "\\"), originalDirectory);
                            if (section.Equals("[core]", StringComparison.OrdinalIgnoreCase) && key.Equals("worktree", StringComparison.OrdinalIgnoreCase)
                                && resolvedContexts.SingleOrDefault(c => c.GitDirectory.Equals(originalDirectory, StringComparison.OrdinalIgnoreCase)) is { } context)
                                path = context.WorkingPath;
                            try { lines[i] = match.Groups[1].Value + "\"" + Map(path).Replace('\\', '/') + "\""; }
                            catch (InvalidDataException) when (section.StartsWith("[include", StringComparison.OrdinalIgnoreCase))
                            { lines[i] = "# Repo Backup: external config include disabled during recovery: " + lines[i]; }
                        }
                        await WriteMetadataAsync(child, string.Join("\n", lines) + "\n");
                    }
                }
            }
        }
        foreach (var root in workingRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Validate where Git actually resolves before allowing its first mutating command.
            var resolved = await git.ReadAsync(root, ["rev-parse", "--path-format=absolute", "--git-dir", "--git-common-dir", "--show-toplevel"], token);
            resolved.EnsureSuccess("Validate restored Git locations");
            foreach (var location in resolved.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var physical = PathSafety.PhysicalPath(location);
                if (!PathSafety.IsWithin(physical, PathSafety.PhysicalPath(recoveryDirectory)) ||
                    !sources.Any(s => PathSafety.IsWithin(physical, PathSafety.PhysicalPath(Restored(s)))))
                    throw new InvalidDataException("Restored Git resolves outside the recovery mappings: " + location);
            }
            (await git.ReadAsync(root, ["worktree", "repair"], token)).EnsureSuccess("Repair restored Git relationships");
            var check = await git.ReadAsync(root, ["worktree", "list", "--porcelain", "-z"], token); check.EnsureSuccess("Validate restored Git worktrees");
            foreach (var field in check.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                if (field.StartsWith("worktree ", StringComparison.Ordinal) && !PathSafety.IsWithin(field[9..], recoveryDirectory)) throw new InvalidDataException("Restored Git worktree still points outside the recovery directory.");
            (await git.ReadAsync(root, ["status", "--porcelain=v1", "-z"], token)).EnsureSuccess("Validate restored working tree");
        }
    }
}
