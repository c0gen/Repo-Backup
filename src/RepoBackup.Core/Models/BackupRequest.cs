namespace RepoBackup.Core.Models;

// Own the configuration lists: edits in the desktop after enqueueing cannot change a request.
public sealed record BackupRequest(string Id, Destination Destination, ProjectEntry Project,
    SavedSelection? Selection, bool UseVss, BackupReadMode ReadMode)
{
    public string Name => Selection?.Name ?? Project.Name;
    public string SeriesId => Selection is null ? "project-" + Project.Id : "selection-" + Selection.Id;
    public string DuplicateKey => Destination.Id + "|" + SeriesId;

    public static BackupRequest Capture(Destination destination, ProjectEntry project, SavedSelection? selection,
        bool useVss, BackupReadMode readMode) => new(Guid.NewGuid().ToString("N"), destination with { },
            project with
            {
                LegacyIds = [.. project.LegacyIds],
                Roots = project.Roots.Select(r => r with { LegacyIds = [.. r.LegacyIds], PreviousPaths = [.. r.PreviousPaths], Aliases = [.. r.Aliases] }).ToList(),
                DiscoverySources = [.. project.DiscoverySources], Exclusions = Copy(project.Exclusions)
            }, selection is null ? null : selection with { Paths = [.. selection.Paths], Exclusions = Copy(selection.Exclusions) }, useVss, readMode);

    private static ExclusionRules Copy(ExclusionRules rules) => rules with
    { ExcludedDirectories = [.. rules.ExcludedDirectories], RelativePatterns = [.. rules.RelativePatterns] };
}
