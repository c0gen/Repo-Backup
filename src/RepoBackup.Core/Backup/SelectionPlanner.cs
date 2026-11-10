using RepoBackup.Core.Git;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using RepoBackup.Core.Windows;

namespace RepoBackup.Core.Backup;

public sealed class SelectionPlanner(GitInspector git)
{
    public async Task<Preview> PreviewAsync(ProjectEntry project, SavedSelection? selection = null, CancellationToken token = default,
        IProgress<SourceScanProgress>? progress = null)
    {
        if (selection is not null && (selection.ProjectId != project.Id || selection.Paths.Count == 0)) throw new ArgumentException("Selection must belong to this project and include a folder or file.");
        if (selection is not null && !Enum.IsDefined(selection.PatternBase)) throw new ArgumentException("Unsupported selection exclusion base.");
        var warnings = new List<string>(); var candidates = new List<(string Path, SourceKind Kind)>();
        var inventory = new GitInventory();
        if (project.IdentityConflict) inventory.Issues.Add(new("project.identity-conflict", BackupIssueSeverity.CoverageError, null,
            "Duplicate source folders have conflicting project settings. Resolve the duplicate configuration before claiming complete coverage."));
        var rules = selection?.Exclusions ?? project.Exclusions;
        var matcher = new ExclusionMatcher(rules);
        if (selection is null)
        {
            foreach (var root in project.Roots)
            {
                candidates.Add((root.Path, SourceKind.Project));
            }
            var identityIssues = inventory.Issues.ToList();
            inventory = await git.InventoryAsync(project.Roots.Select(r => r.Path).Where(Directory.Exists), token);
            inventory.Issues.AddRange(identityIssues);
            candidates.AddRange(inventory.Sources);
        }
        else
        {
            foreach (var path in selection.Paths)
            {
                if (!project.Roots.Any(r => PathSafety.IsWithin(WslLinks.SourcePath(path), PathSafety.PhysicalPath(r.Path)))) throw new InvalidOperationException("Selected path is outside the project's source roots: " + path);
                candidates.Add((PathSafety.Normalize(path), SourceKind.Project));
            }
        }
        // A source nested within another source is stored only once. Mappings remain unambiguous on recovery.
        var metadata = candidates.Where(c => c.Kind == SourceKind.GitMetadata).Select(c => PathSafety.Normalize(c.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var distinct = candidates.DistinctBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var roots = distinct.Where(c => !distinct.Any(other => !other.Path.Equals(c.Path, StringComparison.OrdinalIgnoreCase) && PathSafety.IsWithin(c.Path, other.Path))).ToList();
        var sources = roots.Select((r, i) => new SourceMapping(i.ToString(), PathSafety.Normalize(r.Path), PathSafety.SnapshotPath(r.Path), $"{i + 1:D2}-{PathSafety.SafeName(Path.GetFileName(r.Path))}", r.Kind)).ToList();
        var tracked = inventory.TrackedFiles;
        if (selection is not null)
        {
            var protection = await git.InventoryAsync(project.Roots.Select(r => r.Path).Where(Directory.Exists), token);
            tracked.UnionWith(protection.TrackedFiles);
            // A selection needs tracked protection for its contents, not unavailable unrelated worktrees.
            inventory.Issues.AddRange(protection.Issues.Where(i => i.Severity == BackupIssueSeverity.CoverageError
                && i.Path is not null && Path.IsPathFullyQualified(i.Path) && selection.Paths.Any(p =>
                    PathSafety.IsWithin(p, i.Path) || PathSafety.IsWithin(i.Path, p))));
        }
        var trackedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in tracked)
        {
            var directory = Path.GetDirectoryName(file);
            while (directory is not null && trackedDirectories.Add(directory)) directory = Path.GetDirectoryName(directory);
        }
        var files = new List<FileEntry>(); var excluded = new List<string>(); var links = new List<WslLink>();
        var directories = new List<(SourceMapping Source, string Path)>();
        var reporter = new SourceScanReporter(progress);
        long scannedFiles = 0, scannedBytes = 0;
        var lastPath = "";
        await Task.Run(() =>
        {
            foreach (var source in sources)
            {
                var patternRoot = selection?.PatternBase == ExclusionPatternBase.ProjectRoot
                    ? project.Roots.Where(r => PathSafety.IsWithin(source.OriginalPath, r.Path)).OrderByDescending(r => r.Path.Length).FirstOrDefault()?.Path ?? source.OriginalPath
                    : source.OriginalPath;
                var pending = new Stack<FileSystemInfo>();
                try { pending.Push(SourceScanner.Root(source.OriginalPath)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { inventory.Issues.Add(SourceAvailability.Issue(source.OriginalPath, e, root: true)); continue; }
                while (pending.TryPop(out var entry))
                {
                    var path = entry.FullName;
                    token.ThrowIfCancellationRequested();
                    lastPath = path;
                    reporter.Report(path, scannedFiles, scannedBytes);
                    try
                    {
                        var attributes = entry.Attributes;
                        var isDirectory = (attributes & FileAttributes.Directory) != 0;
                        var isLink = (attributes & FileAttributes.ReparsePoint) != 0;
                        var relative = Path.GetRelativePath(source.OriginalPath, path).Replace('\\', '/');
                        var segments = relative.Split('/');
                        // Git's own metadata is always included in a full project, even if a custom pattern names it.
                        var gitMetadata = source.Kind == SourceKind.GitMetadata || segments.Contains(".git", StringComparer.OrdinalIgnoreCase)
                            || metadata.Any(m => PathSafety.IsWithin(path, m) || isDirectory && PathSafety.IsWithin(m, path));
                        var preserve = tracked.Contains(path) || (isDirectory && trackedDirectories.Contains(path)) || (selection is null && gitMetadata);
                        if (matcher.Matches(Path.GetRelativePath(patternRoot, path)) && !preserve) { if (excluded.Count < 500) excluded.Add(path); continue; }
                        // Excluded placeholders need no hydration. Included placeholders cannot claim local coverage.
                        if ((attributes & FileAttributes.Offline) != 0 || (((int)attributes & (0x400000 | 0x40000)) != 0))
                        { inventory.Issues.Add(new("source.cloud-placeholder", BackupIssueSeverity.CoverageError, path, "Cloud placeholder not locally readable: " + path)); continue; }
                        if (isLink)
                        {
                            var linux = WslLinks.Read(path);
                            if (linux is not null)
                            {
                                links.Add(new(source.Id, relative, linux));
                                files.Add(new(path, 0, DateTime.FromFileTimeUtc(linux.WriteTime).Ticks, true, false, linux.Fingerprint));
                                scannedFiles++;
                                continue;
                            }
                            if (entry.LinkTarget is null) { var message = "Unsupported reparse point: " + path; warnings.Add(message); inventory.Issues.Add(new("source.unsupported-link", BackupIssueSeverity.CoverageError, path, message)); }
                            else { files.Add(new(path, 0, entry.LastWriteTimeUtc.Ticks, true, isDirectory)); if (!isDirectory) scannedFiles++; }
                            continue;
                        }
                        if (isDirectory)
                        {
                            directories.Add((source, path));
                            var count = 0;
                            foreach (var child in SourceScanner.Children(path)) { token.ThrowIfCancellationRequested(); pending.Push(child); count++; }
                            if (count == 0) files.Add(new(path, 0, entry.LastWriteTimeUtc.Ticks, false, true));
                        }
                        else
                        {
                            files.Add(new(path, ((FileInfo)entry).Length, entry.LastWriteTimeUtc.Ticks));
                            scannedFiles++; scannedBytes += ((FileInfo)entry).Length;
                        }
                    }
                    catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { inventory.Issues.Add(SourceAvailability.Issue(path, e)); }
                }
            }
        }, token);
        reporter.Report(lastPath, scannedFiles, scannedBytes, force: true);
        // Directory entries in files-from-raw recurse. Store empty/filtered directories
        // in the manifest instead, so restic can never reintroduce excluded children.
        var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.Where(f => !f.IsDirectory || f.IsLink))
            for (var parent = Path.GetDirectoryName(file.Path); parent is not null && occupied.Add(parent); parent = Path.GetDirectoryName(parent)) { }
        var recordedDirectories = directories.Where(d => !occupied.Contains(d.Path))
            .Select(d => new ManifestDirectory(d.Source.Id, Path.GetRelativePath(d.Source.OriginalPath, d.Path).Replace('\\', '/'))).ToList();
        var issues = inventory.Issues.Concat(warnings.Where(w => !inventory.Issues.Any(i => i.Message == w))
            .Select(w => new BackupIssue("source.warning", BackupIssueSeverity.CoverageError, null, w))).Distinct().ToList();
        var legacyWarnings = warnings.Concat(issues.Where(i => i.Severity == BackupIssueSeverity.CoverageError).Select(i => i.Message)).Distinct().ToList();
        return new(sources, files.DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList(), excluded, legacyWarnings, selection is null ? Coverage.FullProject : Coverage.PartialSelection)
            { WslLinks = links, Directories = recordedDirectories, GitMetadataDirectories = metadata,
                GitContexts = inventory.Contexts, GitRegistrations = inventory.Registrations,
                Issues = issues };
    }

    public static bool Changed(Preview before, Preview after) => FilesChanged(before.Files, after.Files) || ManifestMetadataChanged(before, after);

    private static bool FilesChanged(IEnumerable<FileEntry> before, IEnumerable<FileEntry> after)
    {
        var entries = before.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var file in after)
        {
            count++;
            if (!entries.TryGetValue(file.Path, out var previous) || previous != file) return true;
        }
        return entries.Count != count;
    }

    public static bool ManifestMetadataChanged(Preview before, Preview after) => WslLinksChanged(before, after)
        || !before.Sources.Select(s => (s.OriginalPath, s.Kind)).SequenceEqual(after.Sources.Select(s => (s.OriginalPath, s.Kind)))
        || !before.GitContexts.SequenceEqual(after.GitContexts)
        || !before.GitRegistrations.SequenceEqual(after.GitRegistrations)
        || !before.Directories.OrderBy(d => d.SourceId).ThenBy(d => d.RelativePath).SequenceEqual(after.Directories.OrderBy(d => d.SourceId).ThenBy(d => d.RelativePath))
        || !before.GitMetadataDirectories.SequenceEqual(after.GitMetadataDirectories, StringComparer.OrdinalIgnoreCase);

    public static bool WslLinksChanged(Preview before, Preview after) =>
        FilesChanged(before.Files.Where(f => f.WslFingerprint is not null), after.Files.Where(f => f.WslFingerprint is not null));
}
