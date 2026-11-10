using System.IO.Enumeration;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Backup;

internal sealed class ExclusionMatcher
{
    private readonly HashSet<string> directories;
    private readonly string[] patterns;
    public ExclusionMatcher(ExclusionRules rules)
    {
        directories = rules.ExcludedDirectories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        patterns = rules.RelativePatterns.Select(p => p.Replace('\\', '/')).ToArray();
    }
    public bool Matches(string relative)
    {
        relative = relative.Replace('\\', '/');
        if (relative.Split('/').Any(directories.Contains)) return true;
        // A protected tracked descendant can require traversing an excluded folder.
        // Its untracked siblings must still inherit that folder's exclusion.
        for (var path = relative; ;)
        {
            if (patterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, path, true))) return true;
            var slash = path.LastIndexOf('/');
            if (slash < 0) return false;
            path = path[..slash];
        }
    }
}
