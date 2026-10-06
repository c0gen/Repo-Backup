using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using RepoBackup.Core.Storage;

namespace RepoBackup.Core.Discovery;

public sealed class RepositoryScanner(CatalogStore catalog)
{
    public Task<int> ScanAsync(string location, CancellationToken token = default) => Task.Run(() =>
    {
        var found = 0; var pending = new Stack<string>(); pending.Push(PathSafety.Normalize(location));
        while (pending.TryPop(out var path))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                if (Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git")))
                {
                    catalog.RegisterCandidate(PathSafety.DisplayName(path), [path], "Repository scan"); found++; continue;
                }
                foreach (var directory in Directory.EnumerateDirectories(path))
                    if (!ExclusionRules.Defaults.Contains(Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase)) pending.Push(directory);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return found;
    }, token);
}
