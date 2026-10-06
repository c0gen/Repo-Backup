using RepoBackup.Core.Models;

namespace RepoBackup.Desktop.ViewModels;

public sealed record SnapshotFileItem(SnapshotFile File, string DisplayPath)
{
    public string Path => File.Path;
    public string Type => File.Type switch { "dir" => "Folder", "file" => "File", "symlink" => "Link", _ => File.Type };
}

public static class SnapshotBrowser
{
    public static IEnumerable<SnapshotFileItem> ProjectFiles(IEnumerable<SnapshotFile> files, RecoveryManifest manifest)
    {
        foreach (var file in files)
        {
            var source = manifest.Sources.OrderByDescending(s => s.SnapshotPath.Length).FirstOrDefault(s =>
                file.Path.Equals(s.SnapshotPath, StringComparison.OrdinalIgnoreCase) ||
                file.Path.StartsWith(s.SnapshotPath.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
            if (source is null) continue;
            var name = System.IO.Path.GetFileName(source.OriginalPath.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(name)) name = source.OriginalPath;
            var label = source.Kind switch { SourceKind.Worktree => "Worktree · " + name, SourceKind.GitMetadata => "Git data · " + name, _ => name };
            yield return new(file, label + file.Path[source.SnapshotPath.Length..]);
        }
    }
}
