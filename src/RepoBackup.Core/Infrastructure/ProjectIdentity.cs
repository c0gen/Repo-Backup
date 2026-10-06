using RepoBackup.Core.Discovery;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Infrastructure;

// Folder identity is independent of the program, workspace name, and Git remote.
public static class ProjectIdentity
{
    public static HashSet<string> PathKeys(string path)
    {
        var normalized = PathSafety.Normalize(path);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { normalized };
        try { keys.Add(PathSafety.PhysicalPath(normalized)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return keys;
    }

    public static HashSet<string> Keys(SourceRoot root)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in root.PreviousPaths.Concat(root.Aliases).Prepend(root.Path)) keys.UnionWith(PathKeys(path));
        return keys;
    }

    public static SourceRoot Observe(SourceRoot root, IEnumerable<string> paths)
    {
        var normalized = PathSafety.Normalize(root.Path);
        var previous = root.PreviousPaths.Select(PathSafety.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var aliases = Keys(root);
        foreach (var path in paths) aliases.UnionWith(PathKeys(path));
        aliases.Remove(normalized);
        aliases.ExceptWith(previous);
        return root with { Path = normalized, PreviousPaths = previous, Aliases = aliases.Order(StringComparer.OrdinalIgnoreCase).ToList() };
    }

    public static ProjectEntry Normalize(ProjectEntry project)
    {
        if (string.IsNullOrWhiteSpace(project.Id) || project.Roots is not { Count: > 0 } || project.DiscoverySources is null)
            throw new InvalidDataException("Project needs an identity and at least one source root.");
        if (project.Roots.Any(r => string.IsNullOrWhiteSpace(r.Id) || r.PreviousPaths is null || r.Aliases is null))
            throw new InvalidDataException("Invalid source root identity or aliases.");
        var sources = project.DiscoverySources.Count == 0
            ? new List<DiscoverySource> { new(DiscoveryProviders.FromOrigin(project.Origin), project.ExternalId) }
            : project.DiscoverySources;
        if (sources.Any(s => string.IsNullOrWhiteSpace(s.ProviderId))) throw new InvalidDataException("Invalid discovery provider.");
        return project with
        {
            Roots = project.Roots.Select(r => Observe(r, [])).ToList(),
            DiscoverySources = sources.Select(s => s with { ExternalId = string.IsNullOrWhiteSpace(s.ExternalId) ? null : s.ExternalId }).Distinct().ToList()
        };
    }
}
