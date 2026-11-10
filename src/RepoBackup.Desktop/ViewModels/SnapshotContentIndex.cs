namespace RepoBackup.Desktop.ViewModels;

public sealed class SnapshotContentIndex
{
    private readonly SnapshotFileItem[] files;
    private readonly Dictionary<string, SnapshotFileItem[]> children;
    private readonly Dictionary<string, SnapshotFileItem> byPath;
    public int Count => files.Length;
    public SnapshotFileItem? Find(string? path) => path is not null ? byPath.GetValueOrDefault(path) : null;
    private SnapshotContentIndex(SnapshotFileItem[] files, Dictionary<string, SnapshotFileItem[]> children,
        Dictionary<string, SnapshotFileItem> byPath) { this.files = files; this.children = children; this.byPath = byPath; }

    public static SnapshotContentIndex Build(IEnumerable<SnapshotFileItem> entries, CancellationToken token)
    {
        var nodes = new Dictionary<string, SnapshotFileItem>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            nodes[entry.Path] = entry;
        }
        // Materialize missing logical ancestors without confusing equally named source roots.
        foreach (var entry in nodes.Values.ToArray())
        {
            var path = entry.Path.TrimEnd('/'); var label = entry.DisplayPath.TrimEnd('/');
            while (label.LastIndexOf('/') is var slash && slash > 0 && path.LastIndexOf('/') is var rawSlash && rawSlash > 0)
            {
                path = path[..rawSlash]; label = label[..slash];
                if (!nodes.TryAdd(path, new(new(path, "dir", 0), label))) break;
            }
        }
        token.ThrowIfCancellationRequested();
        var all = nodes.Values.OrderBy(x => x.File.Type == "dir" ? 0 : 1).ThenBy(x => x.DisplayPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Path, StringComparer.Ordinal).ToArray();
        var grouped = new Dictionary<string, List<SnapshotFileItem>>(StringComparer.Ordinal);
        foreach (var file in all)
        {
            token.ThrowIfCancellationRequested();
            var parent = file.DisplayPath.Contains('/') ? file.Path[..file.Path.LastIndexOf('/')] : "";
            if (!grouped.TryGetValue(parent, out var list)) grouped[parent] = list = [];
            list.Add(file);
        }
        return new(all, grouped.ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.Ordinal), nodes);
    }

    public SnapshotFileItem[] Query(string folder, string search, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(search)) return children.GetValueOrDefault(folder) ?? [];
        var matches = new List<SnapshotFileItem>();
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            if (file.DisplayPath.Contains(search, StringComparison.OrdinalIgnoreCase)) matches.Add(file);
        }
        return matches.ToArray();
    }
}
