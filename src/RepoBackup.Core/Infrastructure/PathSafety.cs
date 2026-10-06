using System.Security.Cryptography;
using System.Text;

namespace RepoBackup.Core.Infrastructure;

public static class PathSafety
{
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new ArgumentException("Choose an absolute local or UNC path.");
        var full = Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar));
        return Path.TrimEndingDirectorySeparator(full);
    }

    public static bool IsWithin(string path, string parent)
    {
        path = Normalize(path); parent = Normalize(parent);
        var prefix = parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar;
        return path.Equals(parent, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static string PhysicalPath(string path)
    {
        path = Normalize(path);
        var root = Path.GetPathRoot(path)!;
        var current = root;
        foreach (var part in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!Directory.Exists(current) && !File.Exists(current)) continue;
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                current = info.ResolveLinkTarget(true)?.FullName ?? throw new IOException("Cannot resolve a filesystem link: " + current);
        }
        return Normalize(current);
    }

    public static void EnsureDestinationOutsideSources(string destination, IEnumerable<string> sources)
    {
        var actual = PhysicalPath(destination);
        foreach (var source in sources)
        {
            var sourceActual = PhysicalPath(source);
            if (IsWithin(actual, sourceActual) || IsWithin(sourceActual, actual))
                throw new InvalidOperationException("Backup destination and source trees must not overlap: " + source);
        }
    }

    public static bool IsBroad(string path)
    {
        var normal = Normalize(path);
        return normal.Equals(Path.GetPathRoot(normal), StringComparison.OrdinalIgnoreCase)
            || normal.Equals(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(normal).Equals("Downloads", StringComparison.OrdinalIgnoreCase);
    }

    public static string SnapshotPath(string path)
    {
        var normalized = Normalize(path).Replace('\\', '/');
        if (normalized.StartsWith("//")) return normalized;
        return "/" + normalized.Replace(":", "");
    }

    public static string SafeName(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')).Trim('-') is { Length: > 0 } safe ? safe : "source";
    public static string DisplayName(string path) => Path.GetFileName(Normalize(path)) is { Length: > 0 } name ? name : Normalize(path);
    public static string LockKey(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PhysicalPath(path).ToUpperInvariant())));
    public static long? FreeSpace(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Normalize(path))!).AvailableFreeSpace; }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }
}
