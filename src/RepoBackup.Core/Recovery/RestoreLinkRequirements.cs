using RepoBackup.Core.Models;

namespace RepoBackup.Core.Recovery;

public static class RestoreLinkRequirements
{
    public static void EnsureSupported(string target, IEnumerable<SnapshotFile> nodes, Action<string>? createLink = null)
    {
        var first = nodes.FirstOrDefault(n => n.Type == "symlink");
        if (first is null) return;
        var probe = Path.Combine(target, ".link-probe-" + Guid.NewGuid().ToString("N"));
        var link = Path.Combine(probe, "link");
        Directory.CreateDirectory(probe);
        try
        {
            (createLink ?? (path => Directory.CreateSymbolicLink(path, ".")))(link);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new IOException("Windows cannot recreate symbolic links on the recovery volume. " +
                "Run Repo Backup as Administrator or enable Windows Developer Mode, and use a filesystem that supports symbolic links. " +
                "Retry into a new directory. First affected snapshot path: " + first.Path + ". " + e.Message, e);
        }
        finally
        {
            // Remove only this private probe and its link; never traverse the link target.
            try { Directory.Delete(link); } catch (DirectoryNotFoundException) { }
            Directory.Delete(probe);
        }
    }
}
