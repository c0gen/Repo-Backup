using RepoBackup.Core.Backup;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Recovery;

// One logical file tree for browsing and recovery, including entries stored in the manifest.
public static class SnapshotContents
{
    public static List<SnapshotFile> Merge(RecoveryManifest manifest, IReadOnlyList<SnapshotFile> native)
    {
        manifest = LegacySnapshotMappings.Adapt(manifest, native);
        if (manifest.WslLinks.Count == 0 && manifest.Directories.Count == 0) return [.. native];
        var nodes = new Dictionary<string, SnapshotFile>(StringComparer.OrdinalIgnoreCase);
        var nativeAncestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in native)
        {
            if (!nodes.TryAdd(node.Path, node)) throw new InvalidDataException("Duplicate snapshot path: " + node.Path);
            for (var parent = WslLinkMetadata.Parent(node.Path); parent.Length > 0 && nativeAncestors.Add(parent); parent = WslLinkMetadata.Parent(parent)) { }
        }
        foreach (var link in manifest.WslLinks)
        {
            var source = manifest.Sources.Single(s => s.Id == link.SourceId);
            var path = WslLinkMetadata.SnapshotPath(source, link);
            if (nodes.ContainsKey(path) || nativeAncestors.Contains(path))
                throw new InvalidDataException("WSL link collides with snapshot contents: " + path);
            nodes.Add(path, new(path, "symlink", 0));
            for (var parent = WslLinkMetadata.Parent(path); parent.Length > 0; parent = WslLinkMetadata.Parent(parent))
            {
                if (nodes.TryGetValue(parent, out var existing) && existing.Type != "dir")
                    throw new InvalidDataException("WSL link has a non-directory snapshot ancestor: " + parent);
                nodes.TryAdd(parent, new(parent, "dir", 0));
            }
        }
        foreach (var directory in manifest.Directories)
        {
            var source = manifest.Sources.Single(s => s.Id == directory.SourceId);
            for (var path = ManifestPaths.SnapshotPath(source, directory.RelativePath); path.Length > 0; path = WslLinkMetadata.Parent(path))
            {
                if (nodes.TryGetValue(path, out var existing) && existing.Type != "dir")
                    throw new InvalidDataException("Manifest directory collides with snapshot contents: " + path);
                nodes.TryAdd(path, new(path, "dir", 0));
            }
        }
        return nodes.Values.OrderBy(n => n.Path, StringComparer.Ordinal).ToList();
    }

    public static async Task<List<SnapshotFile>> ListAsync(ResticClient restic, Destination destination, string snapshotId, CancellationToken token = default)
    {
        var snapshot = (await restic.SnapshotSummariesAsync(destination, token)).Single(s => s.Id.StartsWith(snapshotId, StringComparison.Ordinal));
        var manifest = await restic.ManifestAsync(destination, snapshot, token);
        return Merge(manifest, await restic.FilesAsync(destination, snapshot.Id, token));
    }
}
