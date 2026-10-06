using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Recovery;

public static class RecoveryMetadata
{
    public static void Validate(RecoveryManifest manifest, SnapshotInfo snapshot)
    {
        if (manifest.Project is not { } project || string.IsNullOrWhiteSpace(project.Id) || project.Roots is not { Count: > 0 } ||
            project.Roots.Any(r => r is null || string.IsNullOrWhiteSpace(r.Id)) ||
            project.Roots.Select(r => r.Id).Distinct().Count() != project.Roots.Count || manifest.Coverage != snapshot.Coverage)
            throw new InvalidDataException("Invalid project recovery metadata.");
        if (project.Roots.Select(r => PathSafety.Normalize(r.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != project.Roots.Count)
            throw new InvalidDataException("Repeated project root in recovery metadata.");
        if (manifest.Selection is { } selection)
        {
            if (string.IsNullOrWhiteSpace(selection.Id) || selection.ProjectId != project.Id || selection.Paths is not { Count: > 0 } || manifest.SeriesId != "selection-" + selection.Id || manifest.Coverage != Coverage.PartialSelection ||
                selection.Paths.Any(path => !project.Roots.Any(r => PathSafety.IsWithin(path, r.Path))))
                throw new InvalidDataException("Invalid saved-selection recovery metadata.");
        }
        else if (manifest.SeriesId != "project-" + manifest.Project.Id || manifest.Coverage != Coverage.FullProject)
            throw new InvalidDataException("Invalid project recovery series.");
    }
}
