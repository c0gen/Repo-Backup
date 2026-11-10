using System.Text;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Recovery;

public sealed record RestorePathPlan(SourceMapping Source, string Tree, string? Include, IReadOnlyList<SnapshotFile> Expected)
{
    public string TargetPath(string target, SnapshotFile node) => Path.Combine(target, "sources", Source.RestoreFolder,
        node.Path[Tree.Length..].TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

    public void Verify(string target, CancellationToken token = default)
    {
        foreach (var node in Expected)
        {
            token.ThrowIfCancellationRequested();
            RestorePaths.VerifyNode(TargetPath(target, node), node);
        }
    }
}

public static class RestorePaths
{
    public static void VerifyNode(string path, SnapshotFile node)
    {
        if (node.Type == "symlink")
        {
            if (!File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Requested snapshot link was not restored: " + node.Path);
            return;
        }
        if (node.Type == "dir" ? !Directory.Exists(path) : !File.Exists(path) && !Directory.Exists(path))
            throw new IOException("Requested snapshot contents were not restored: " + node.Path);
        if (node.Type == "file" && new FileInfo(path).Length != node.Size)
            throw new IOException("Restored file size does not match the snapshot: " + node.Path);
    }

    public static void CreateManifestDirectories(RestorePathPlan plan, IReadOnlySet<string> nativePaths, string target)
    {
        foreach (var directory in plan.Expected.Where(n => n.Type == "dir" && !nativePaths.Contains(n.Path)))
            ManifestPaths.CreateDirectory(plan.TargetPath(target, directory), target);
    }

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
            ?? throw new ArgumentException("The recorded source is missing from this snapshot: " + source.SnapshotPath + ". This older or incomplete backup cannot recreate omitted data. Select an available file or source folder to recover its contents.");
        var requested = selected ?? source.SnapshotPath;
        var node = nodes.SingleOrDefault(n => n.Path == requested)
            ?? throw new ArgumentException("The requested path does not exist in this snapshot: " + requested);
        if (requested != source.SnapshotPath && !requested.StartsWith(source.SnapshotPath.TrimEnd('/') + "/", StringComparison.Ordinal))
            throw new ArgumentException("Choose a file or folder inside a recorded project source.");
        var tree = sourceNode.Type == "dir" ? source.SnapshotPath : source.SnapshotPath[..source.SnapshotPath.LastIndexOf('/')];
        var relative = requested[tree.Length..];
        var expected = nodes.Where(n => n.Path == requested || node.Type == "dir" && n.Path.StartsWith(requested.TrimEnd('/') + "/", StringComparison.Ordinal)).ToList();
        var collision = expected.GroupBy(n => n.Path, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (collision is not null) throw new InvalidDataException("Snapshot paths collide on Windows: " + string.Join("; ", collision.Select(n => n.Path)));
        return new(source, tree, relative.Length == 0 ? null : LiteralFilter(relative), expected);
    }
}
