using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Recovery;

internal static class ManifestPaths
{
    public static string SnapshotPath(SourceMapping source, string relative) => relative == "."
        ? source.SnapshotPath : source.SnapshotPath.TrimEnd('/') + "/" + relative;

    public static void ValidateRelative(string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative != "." && relative.Split('/').Any(p => p.Length == 0 || p is "." or ".." ||
            p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("Unsafe manifest relative path.");
    }

    public static void ValidateSources(RecoveryManifest manifest)
    {
        if (manifest.Sources is null || manifest.SchemaVersion is not (1 or 2 or 3 or 4)) throw new InvalidDataException("Invalid recovery manifest version or sources.");
        if (manifest.Sources.Any(s => s is null || string.IsNullOrEmpty(s.Id) || string.IsNullOrEmpty(s.RestoreFolder) ||
            s.RestoreFolder != PathSafety.SafeName(s.RestoreFolder) || !ValidSnapshotPath(s, manifest.SchemaVersion)) ||
            manifest.Sources.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != manifest.Sources.Count ||
            manifest.Sources.Select(s => s.RestoreFolder).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Sources.Count)
            throw new InvalidDataException("Invalid or repeated source mapping.");
        if (manifest.Directories is null || manifest.GitMetadataDirectories is null ||
            manifest.SchemaVersion < 3 && (manifest.Directories.Count > 0 || manifest.GitMetadataDirectories.Count > 0 || manifest.Selection?.PatternBase == ExclusionPatternBase.ProjectRoot))
            throw new InvalidDataException("Directory and Git metadata require recovery manifest version 3.");
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in manifest.Directories)
        {
            if (directory is null) throw new InvalidDataException("Invalid manifest directory.");
            ValidateRelative(directory.RelativePath);
            var source = manifest.Sources.SingleOrDefault(s => s.Id == directory.SourceId) ?? throw new InvalidDataException("Unknown manifest directory source.");
            if (!directories.Add(SnapshotPath(source, directory.RelativePath))) throw new InvalidDataException("Duplicate manifest directory.");
        }
        foreach (var metadata in manifest.GitMetadataDirectories)
            if (!manifest.Sources.Any(s => PathSafety.IsWithin(metadata, s.OriginalPath)))
                throw new InvalidDataException("Git metadata is outside recorded sources.");
        GitManifestMetadata.Validate(manifest);
    }

    public static bool ValidSnapshotPath(SourceMapping source, int schemaVersion)
    {
        if (source.SnapshotPath.Equals(PathSafety.SnapshotPath(source.OriginalPath), schemaVersion < 4 ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return true;
        if (schemaVersion >= 4) return false;
        var legacy = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.OriginalPath.Replace('/', '\\'))).Replace('\\', '/');
        return source.SnapshotPath == (legacy.StartsWith("//", StringComparison.Ordinal) ? legacy : "/" + legacy.Replace(":", ""));
    }

    public static void CreateDirectory(string path, string restoreRoot)
    {
        path = PathSafety.Normalize(path); restoreRoot = PathSafety.Normalize(restoreRoot);
        if (!PathSafety.IsWithin(path, restoreRoot)) throw new InvalidDataException("Manifest directory would escape the restore directory.");
        // Resolve pre-existing ancestors (including paths outside the restore root),
        // then reject links inside the newly created recovery tree.
        for (var parent = path; PathSafety.IsWithin(parent, restoreRoot); parent = Path.GetDirectoryName(parent)!)
        {
            try
            {
                var attributes = File.GetAttributes(parent);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                    throw new InvalidDataException("Unsafe manifest directory ancestor: " + parent);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            if (parent.Equals(restoreRoot, StringComparison.OrdinalIgnoreCase)) break;
        }
        Directory.CreateDirectory(path);
    }
}
