using System.Text.Json;
using RepoBackup.Core.Infrastructure;

namespace RepoBackup.Core.Discovery;

public static class WorkspaceFileReader
{
    public static string? LocalPath(string reference)
    {
        if (Path.IsPathFullyQualified(reference)) return PathSafety.Normalize(reference);
        if (!Uri.TryCreate(reference, UriKind.Absolute, out var uri) || !uri.IsFile || uri.Query.Length != 0 || uri.Fragment.Length != 0) return null;
        // Antigravity escapes the drive colon. Uri.LocalPath leaves a leading slash
        // in that format, so decode the URI path once before applying Windows rules.
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (uri.Host.Length > 0 && !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            path = "\\\\" + uri.Host + path.Replace('/', '\\');
        else
        {
            if (path.Length >= 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':') path = path[1..];
            path = path.Replace('/', '\\');
        }
        return Path.IsPathFullyQualified(path) ? PathSafety.Normalize(path) : null;
    }

    public static List<string> ParseFolders(string json, string workspacePath)
    {
        workspacePath = PathSafety.Normalize(workspacePath);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("folders", out var folders) || folders.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Invalid workspace folder list.");
        var roots = new List<string>(); var directory = Path.GetDirectoryName(workspacePath)!;
        foreach (var folder in folders.EnumerateArray())
        {
            if (folder.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid workspace folder.");
            string? path;
            if (folder.TryGetProperty("uri", out var uri)) path = LocalPath(uri.GetString() ?? "");
            else if (folder.TryGetProperty("path", out var value))
            {
                var text = value.GetString();
                if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Invalid workspace folder path.");
                path = LocalPath(text);
                if (path is null && !Uri.TryCreate(text, UriKind.Absolute, out _) && !Path.IsPathRooted(text))
                    path = PathSafety.Normalize(Path.GetFullPath(text, directory));
            }
            else throw new InvalidDataException("Workspace folder has no path or URI.");
            if (path is not null) roots.Add(path);
        }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static async Task<DiscoveredProject?> ReadReferenceAsync(string reference, bool workspace, CancellationToken token = default)
    {
        var path = LocalPath(reference); if (path is null) return null;
        workspace |= path.EndsWith(".code-workspace", StringComparison.OrdinalIgnoreCase) && !Directory.Exists(path);
        var roots = workspace ? ParseFolders(await DiscoveryFiles.ReadAsync(path, token).ConfigureAwait(false), path) : [path];
        if (roots.Count == 0) return null;
        return new((workspace ? "workspace:" : "folder:") + DiscoveryFiles.PathId(path),
            workspace ? Path.GetFileNameWithoutExtension(path) : PathSafety.DisplayName(path), roots);
    }
}
