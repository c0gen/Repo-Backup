using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Recovery;

internal static class GitManifestMetadata
{
    public static void Validate(RecoveryManifest manifest)
    {
        if (manifest.GitContexts is null || manifest.GitRegistrations is null || manifest.Issues is null
            || manifest.SchemaVersion < 4 && (manifest.GitContexts.Count > 0 || manifest.GitRegistrations.Count > 0 || manifest.Issues.Count > 0))
            throw new InvalidDataException("Git relationships and structured issues require recovery manifest version 4.");
        bool Captured(string path) => Path.IsPathFullyQualified(path) && manifest.Sources.Any(s => PathSafety.IsWithin(path, s.OriginalPath));
        if (manifest.GitContexts.Any(c => c is null || !Captured(c.WorkingPath) || !Captured(c.GitDirectory) || !Captured(c.CommonDirectory))
            || manifest.GitContexts.Select(c => PathSafety.Normalize(c.WorkingPath)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.GitContexts.Count)
            throw new InvalidDataException("Git relationship is outside captured sources or repeated.");
        if (manifest.GitRegistrations.Any(r => r is null || !Captured(r.CommonDirectory) || !Captured(r.AdministrativeDirectory)
            || !PathSafety.IsWithin(r.AdministrativeDirectory, Path.Combine(r.CommonDirectory, "worktrees"))
            || string.IsNullOrWhiteSpace(r.WorktreePath) || r.Available && !Captured(r.WorktreePath))
            || manifest.GitRegistrations.Select(r => PathSafety.Normalize(r.AdministrativeDirectory)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.GitRegistrations.Count)
            throw new InvalidDataException("Invalid captured Git worktree registration.");
        if (manifest.Issues.Any(i => i is null || !Enum.IsDefined(i.Severity) || string.IsNullOrWhiteSpace(i.Code)))
            throw new InvalidDataException("Invalid structured backup issue.");
    }
}
