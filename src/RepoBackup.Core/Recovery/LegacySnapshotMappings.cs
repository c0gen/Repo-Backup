using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Recovery;

internal static class LegacySnapshotMappings
{
    public static RecoveryManifest Adapt(RecoveryManifest manifest, IReadOnlyList<SnapshotFile> native)
    {
        WslLinkMetadata.Validate(manifest);
        if (manifest.SchemaVersion >= 4) return manifest;
        var possibleRoots = manifest.Sources.SelectMany(s => new[] { s.SnapshotPath, PathSafety.SnapshotPath(s.OriginalPath) })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nativeRoots = native.Where(n => possibleRoots.Contains(n.Path)).Select(n => n.Path).ToList();
        return manifest with { Sources = manifest.Sources.Select(source =>
        {
            var canonical = PathSafety.SnapshotPath(source.OriginalPath);
            canonical = nativeRoots.FirstOrDefault(p => p.Equals(canonical, StringComparison.OrdinalIgnoreCase)) ?? canonical;
            // Older manifests recorded the Windows namespace prefix even though restic removed it.
            return source.SnapshotPath != canonical && !nativeRoots.Contains(source.SnapshotPath) ? source with { SnapshotPath = canonical } : source;
        }).ToList() };
    }

    public static string? AdaptSelection(RecoveryManifest original, RecoveryManifest adapted, string? selected)
    {
        if (selected is null) return null;
        var match = original.Sources.SelectMany(s => new[] { (Source: s, Prefix: s.SnapshotPath), (Source: s, Prefix: PathSafety.SnapshotPath(s.OriginalPath)) })
            .OrderByDescending(s => s.Prefix.Length).FirstOrDefault(s => selected.Equals(s.Prefix, StringComparison.OrdinalIgnoreCase)
                || selected.StartsWith(s.Prefix.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
        if (match.Source is null) return selected;
        var current = adapted.Sources.Single(s => s.Id == match.Source.Id);
        return current.SnapshotPath + selected[match.Prefix.Length..];
    }
}
