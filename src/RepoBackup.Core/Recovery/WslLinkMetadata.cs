using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Recovery;

public static class WslLinkMetadata
{
    public static string SnapshotPath(SourceMapping source, WslLink link) => ManifestPaths.SnapshotPath(source, link.RelativePath);

    public static void Validate(RecoveryManifest manifest)
    {
        ManifestPaths.ValidateSources(manifest);
        if (manifest.WslLinks is null ||
            manifest.SchemaVersion == 1 && manifest.WslLinks.Count != 0)
            throw new InvalidDataException("Invalid WSL link manifest version or collection.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in manifest.WslLinks)
        {
            if (link is null || link.Data is null || string.IsNullOrEmpty(link.RelativePath)) throw new InvalidDataException("Invalid WSL link record.");
            var source = manifest.Sources.SingleOrDefault(s => s.Id == link.SourceId) ?? throw new InvalidDataException("Unknown WSL link source.");
            ManifestPaths.ValidateRelative(link.RelativePath);
            link.Data.Validate();
            if (!paths.Add(SnapshotPath(source, link))) throw new InvalidDataException("Duplicate WSL link path.");
        }
        foreach (var path in paths)
            for (var parent = Parent(path); parent.Length > 0; parent = Parent(parent))
                if (paths.Contains(parent)) throw new InvalidDataException("A WSL link cannot contain another link.");
    }

    internal static string Parent(string path) => path.LastIndexOf('/') is var index && index > 0 ? path[..index] : "";
}
