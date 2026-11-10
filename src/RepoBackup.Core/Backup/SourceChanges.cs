using RepoBackup.Core.Models;

namespace RepoBackup.Core.Backup;

public static class SourceChanges
{
    public static SourceChangeSummary Compare(Preview before, Preview after, bool metadataOnly = false)
    {
        var samples = new List<string>(); long added = 0, removed = 0, modified = 0, metadata = 0;
        void Sample(string kind, string path) { if (samples.Count < 100) samples.Add(kind + ": " + path); }
        // VSS captures ordinary contents atomically, but the prepared exact-file list must
        // remain stable (for example, a new Git index must not reference omitted new objects).
        var previous = before.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var file in after.Files)
        {
            if (!previous.Remove(file.Path, out var old)) { added++; Sample("Added", file.Path); }
            else if (old != file && (!metadataOnly || old.WslFingerprint != file.WslFingerprint
                || old.IsLink != file.IsLink || old.IsDirectory != file.IsDirectory)) { modified++; Sample("Modified", file.Path); }
        }
        foreach (var file in previous.Values) { removed++; Sample("Removed", file.Path); }
        var directories = before.Directories.Select(d => d.SourceId + ":" + d.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        directories.SymmetricExceptWith(after.Directories.Select(d => d.SourceId + ":" + d.RelativePath));
        foreach (var path in directories) { metadata++; Sample("Directory metadata", path); }
        var sourceMetadata = before.GitMetadataDirectories.Concat(before.Sources.Select(s => s.OriginalPath + ":" + s.Kind))
            .Concat(before.GitContexts.Select(c => "Git: " + c)).Concat(before.GitRegistrations.Select(r => "Registration: " + r)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        sourceMetadata.SymmetricExceptWith(after.GitMetadataDirectories.Concat(after.Sources.Select(s => s.OriginalPath + ":" + s.Kind))
            .Concat(after.GitContexts.Select(c => "Git: " + c)).Concat(after.GitRegistrations.Select(r => "Registration: " + r)));
        foreach (var path in sourceMetadata) { metadata++; Sample("Source metadata", path); }
        return new(added, removed, modified, metadata, samples);
    }
}
