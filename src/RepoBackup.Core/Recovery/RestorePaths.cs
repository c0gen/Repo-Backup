using System.Text;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Recovery;

public sealed record RestorePathPlan(SourceMapping Source, string Tree, string? Include, IReadOnlyList<SnapshotFile> Expected)
{
    public string TargetPath(string target, SnapshotFile node) => Path.Combine(target, "sources", Source.RestoreFolder,
        node.Path[Tree.Length..].TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

    public void Verify(string target)
    {
        foreach (var node in Expected)
        {
            var path = TargetPath(target, node);
            if (node.Type == "dir" ? !Directory.Exists(path) : !File.Exists(path) && !Directory.Exists(path))
                throw new IOException("Requested snapshot contents were not restored: " + node.Path);
            if (node.Type == "file" && new FileInfo(path).Length != node.Size)
                throw new IOException("Restored file size does not match the snapshot: " + node.Path);
        }
    }
}

public static class RestorePaths
{
    // Restic filters use glob syntax. Character classes work on Windows too.
    public static string LiteralFilter(string path)
    {
        var result = new StringBuilder();
        foreach (var c in path) result.Append(c switch { '[' => "[[]", '*' => "[*]", '?' => "[?]", _ => c.ToString() });
        return result.ToString();
    }

    public static RestorePathPlan Plan(SourceMapping source, IReadOnlyList<SnapshotFile> nodes, string? selected = null)
    {
        var sourceNode = nodes.SingleOrDefault(n => n.Path == source.SnapshotPath)
            ?? throw new ArgumentException("The recorded source is missing from this snapshot: " + source.SnapshotPath);
        var requested = selected ?? source.SnapshotPath;
        var node = nodes.SingleOrDefault(n => n.Path == requested)
            ?? throw new ArgumentException("The requested path does not exist in this snapshot: " + requested);
        if (requested != source.SnapshotPath && !requested.StartsWith(source.SnapshotPath.TrimEnd('/') + "/", StringComparison.Ordinal))
            throw new ArgumentException("Choose a file or folder inside a recorded project source.");
        var tree = sourceNode.Type == "dir" ? source.SnapshotPath : source.SnapshotPath[..source.SnapshotPath.LastIndexOf('/')];
        var relative = requested[tree.Length..];
        var expected = nodes.Where(n => n.Path == requested || node.Type == "dir" && n.Path.StartsWith(requested.TrimEnd('/') + "/", StringComparison.Ordinal)).ToList();
        return new(source, tree, relative.Length == 0 ? null : LiteralFilter(relative), expected);
    }
}
