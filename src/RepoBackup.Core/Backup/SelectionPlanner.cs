using System.IO.Enumeration;
using RepoBackup.Core.Git;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Backup;

public sealed class SelectionPlanner(GitInspector git)
{
    public async Task<Preview> PreviewAsync(ProjectEntry project, SavedSelection? selection = null, CancellationToken token = default)
    {
        if (selection is not null && (selection.ProjectId != project.Id || selection.Paths.Count == 0)) throw new ArgumentException("Selection must belong to this project and include a folder or file.");
        var warnings = new List<string>(); var candidates = new List<(string Path, SourceKind Kind)>();
        var rules = selection?.Exclusions ?? project.Exclusions;
        if (selection is null)
        {
            foreach (var root in project.Roots)
            {
                candidates.Add((root.Path, SourceKind.Project));
                if (Directory.Exists(root.Path)) candidates.AddRange(await git.AssociatedSourcesAsync(root.Path, warnings, token));
            }
        }
        else
        {
            foreach (var path in selection.Paths)
            {
                if (!project.Roots.Any(r => PathSafety.IsWithin(PathSafety.PhysicalPath(path), PathSafety.PhysicalPath(r.Path)))) throw new InvalidOperationException("Selected path is outside the project's source roots: " + path);
                candidates.Add((PathSafety.Normalize(path), SourceKind.Project));
            }
        }
        // A source nested within another source is stored only once. Mappings remain unambiguous on recovery.
        var distinct = candidates.DistinctBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var roots = distinct.Where(c => !distinct.Any(other => !other.Path.Equals(c.Path, StringComparison.OrdinalIgnoreCase) && PathSafety.IsWithin(c.Path, other.Path))).ToList();
        var sources = roots.Select((r, i) => new SourceMapping(i.ToString(), PathSafety.Normalize(r.Path), PathSafety.SnapshotPath(r.Path), $"{i + 1:D2}-{PathSafety.SafeName(Path.GetFileName(r.Path))}", r.Kind)).ToList();
        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in project.Roots.Where(r => Directory.Exists(r.Path)))
        {
            try { tracked.UnionWith(await git.TrackedFilesAsync(root.Path, token)); }
            catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { warnings.Add("Tracked-file protection unavailable: " + e.Message); }
        }
        foreach (var root in roots.Where(r => r.Kind == SourceKind.Worktree))
        {
            try { tracked.UnionWith(await git.TrackedFilesAsync(root.Path, token)); }
            catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { warnings.Add("Tracked-file protection unavailable: " + e.Message); }
        }
        var trackedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in tracked)
        {
            var directory = Path.GetDirectoryName(file);
            while (directory is not null && trackedDirectories.Add(directory)) directory = Path.GetDirectoryName(directory);
        }
        var files = new List<FileEntry>(); var excluded = new List<string>();
        await Task.Run(() =>
        {
            foreach (var source in sources)
            {
                var pending = new Stack<string>(); pending.Push(source.OriginalPath);
                while (pending.TryPop(out var path))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var attributes = File.GetAttributes(path);
                        var isDirectory = (attributes & FileAttributes.Directory) != 0;
                        var isLink = (attributes & FileAttributes.ReparsePoint) != 0;
                        // OneDrive placeholders may hydrate when read. Report them instead of claiming local coverage.
                        if ((attributes & FileAttributes.Offline) != 0 || (((int)attributes & (0x400000 | 0x40000)) != 0)) { warnings.Add("Cloud placeholder not locally readable: " + path); continue; }
                        var relative = Path.GetRelativePath(source.OriginalPath, path).Replace('\\', '/');
                        var segments = relative.Split('/');
                        var cache = segments.Any(s => rules.ExcludedDirectories.Contains(s, StringComparer.OrdinalIgnoreCase));
                        var custom = rules.RelativePatterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern.Replace('\\', '/'), relative, true));
                        // Git's own metadata is always included in a full project, even if a custom pattern names it.
                        var gitMetadata = source.Kind == SourceKind.GitMetadata || segments.Contains(".git", StringComparer.OrdinalIgnoreCase);
                        var preserve = tracked.Contains(path) || (isDirectory && trackedDirectories.Contains(path)) || (selection is null && gitMetadata);
                        if ((cache || custom) && !preserve) { if (excluded.Count < 500) excluded.Add(path); continue; }
                        if (isLink)
                        {
                            FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
                            if (info.LinkTarget is null) warnings.Add("Unsupported reparse point: " + path);
                            else files.Add(new(path, 0, info.LastWriteTimeUtc.Ticks, true, isDirectory));
                            continue;
                        }
                        if (isDirectory)
                        {
                            var children = Directory.EnumerateFileSystemEntries(path).ToList();
                            if (children.Count == 0) files.Add(new(path, 0, Directory.GetLastWriteTimeUtc(path).Ticks, false, true));
                            else foreach (var child in children) pending.Push(child);
                        }
                        else
                        {
                            var info = new FileInfo(path); files.Add(new(path, info.Length, info.LastWriteTimeUtc.Ticks));
                        }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warnings.Add("Source unavailable: " + path + " — " + e.Message); }
                }
            }
        }, token);
        return new(sources, files.DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList(), excluded, warnings.Distinct().ToList(), selection is null ? Coverage.FullProject : Coverage.PartialSelection);
    }

    public static bool Changed(Preview before, Preview after) =>
        before.Files.Count != after.Files.Count || before.Files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Zip(after.Files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)).Any(pair => pair.First != pair.Second);
}
