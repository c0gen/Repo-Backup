using System.Collections.ObjectModel;
using System.Windows.Input;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Windows;
using RepoBackup.Desktop.Infrastructure;

namespace RepoBackup.Desktop.Views;

// The saved set owns selection state; loading or retrying a visual branch never removes it.
public sealed class FolderNode : ObservableObject
{
    public string Path { get; }
    public string Name { get; }
    public FolderNode? Parent { get; }
    public ObservableCollection<FolderNode> Children { get; } = [];
    public ICommand RetryCommand { get; }
    private readonly HashSet<string> saved;
    private bool included, loaded, updating;
    private string error = "";
    public string Error { get => error; private set { if (Set(ref error, value)) Raise(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;
    public bool Included
    {
        get => included;
        set
        {
            if (!Set(ref included, value) || Path.Length == 0) return;
            saved.RemoveWhere(p => PathSafety.IsWithin(p, Path));
            if (value) saved.Add(Path);
            updating = true;
            try { foreach (var child in Children) child.Included = value; }
            finally { updating = false; }
            Parent?.ChildChanged();
        }
    }

    public FolderNode(string path, HashSet<string> saved, FolderNode? parent = null)
    {
        Path = path; Name = System.IO.Path.GetFileName(path) is { Length: > 0 } name ? name : path;
        this.saved = saved; Parent = parent;
        included = saved.Any(p => PathSafety.IsWithin(path, p));
        RetryCommand = new RelayCommand(_ => Load(true), _ => HasError);
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0) Children.Add(new FolderNode());
        }
        catch (Exception e) when (IsReadError(e)) { Error = "Unavailable: " + e.Message; }
    }

    private FolderNode()
    {
        Path = ""; Name = "Expand to load folders"; saved = [];
        RetryCommand = new RelayCommand(_ => { }, _ => false);
    }

    private void ChildChanged()
    {
        if (!loaded || updating) return;
        var all = Children.Count > 0 && Children.All(c => c.Included);
        if (included == all) return;
        included = all;
        saved.Remove(Path);
        if (all) saved.Add(Path);
        else foreach (var child in Children.Where(c => c.Included)) saved.Add(child.Path);
        Raise(nameof(Included)); Parent?.ChildChanged();
    }

    public void Load(bool retry = false)
    {
        if ((!retry && loaded) || Path.Length == 0) return;
        Error = "";
        try
        {
            var attributes = File.GetAttributes(Path);
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                if ((attributes & FileAttributes.ReparsePoint) != 0) _ = WslLinks.Read(Path);
                return;
            }
            var entries = Directory.EnumerateFileSystemEntries(Path).Order(StringComparer.OrdinalIgnoreCase).ToList();
            var children = new List<FolderNode>();
            foreach (var path in entries)
            {
                try
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 && WslLinks.Read(path) is null) continue;
                    children.Add(new(path, saved, this));
                }
                catch (Exception e) when (IsReadError(e)) { children.Add(new FolderNode(path, saved, this) { Error = "Unavailable: " + e.Message }); }
            }
            // Surface saved paths even when the directory listing can no longer see them.
            foreach (var path in saved.Where(p => !p.Equals(Path, StringComparison.OrdinalIgnoreCase) && PathSafety.IsWithin(p, Path)).ToList())
            {
                var first = System.IO.Path.GetRelativePath(Path, path).Split(System.IO.Path.DirectorySeparatorChar)[0];
                var child = System.IO.Path.Combine(Path, first);
                if (children.All(c => !c.Path.Equals(child, StringComparison.OrdinalIgnoreCase))) children.Add(new(child, saved, this));
            }
            Children.Clear(); foreach (var child in children.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)) Children.Add(child);
            loaded = true;
        }
        catch (Exception e) when (IsReadError(e)) { loaded = false; Error = "Unavailable: " + e.Message; }
    }

    public IEnumerable<string> SelectedPaths() => saved.Where(p => Path.Length > 0 && PathSafety.IsWithin(p, Path)).ToList();
    private static bool IsReadError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException;
}
