using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using RepoBackup.Core.Models;
using RepoBackup.Desktop.Infrastructure;

namespace RepoBackup.Desktop.Views;

public sealed class FolderNode : ObservableObject
{
    public string Path { get; }
    public string Name { get; }
    private bool included;
    public bool Included
    {
        get => included;
        set
        {
            if (!Set(ref included, value)) return;
            if (!value && Path.Length > 0) saved.RemoveWhere(p => Core.Infrastructure.PathSafety.IsWithin(p, Path));
            foreach (var child in Children) child.Included = value;
            Parent?.ChildChanged();
        }
    }
    public FolderNode? Parent { get; }
    private void ChildChanged()
    {
        if (!loaded) return;
        var all = Children.Count > 0 && Children.All(c => c.Included);
        if (included == all) return;
        included = all; if (!all) saved.Remove(Path); Raise(nameof(Included)); Parent?.ChildChanged();
    }
    public ObservableCollection<FolderNode> Children { get; } = [];
    private bool loaded;
    private readonly HashSet<string> saved;
    public FolderNode(string path, HashSet<string> saved, FolderNode? parent = null)
    {
        Path = path; Name = System.IO.Path.GetFileName(path) is { Length: > 0 } name ? name : path; this.saved = saved; Parent = parent;
        included = parent?.Included == true || saved.Contains(path);
        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) Children.Add(new FolderNode());
    }
    private FolderNode() { Path = ""; Name = "Expand to load folders"; saved = []; }
    public void Load()
    {
        if (loaded || Path.Length == 0 || !Directory.Exists(Path)) return;
        loaded = true; Children.Clear();
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                Children.Add(new FolderNode(path, saved, this));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    public IEnumerable<string> SelectedPaths()
    {
        if (Included && Path.Length > 0) { yield return Path; yield break; }
        foreach (var child in Children) foreach (var path in child.SelectedPaths()) yield return path;
        // Preserve paths in still-collapsed branches of an existing selection.
        if (!loaded && Path.Length > 0) foreach (var path in saved.Where(p => Core.Infrastructure.PathSafety.IsWithin(p, Path))) yield return path;
    }
}
public partial class FolderSelectionDialog : Window
{
    private readonly ProjectEntry project;
    private readonly SavedSelection? existing;
    private readonly List<FolderNode> roots;
    public SavedSelection? Selection { get; private set; }
    public FolderSelectionDialog(ProjectEntry project, SavedSelection? existing)
    {
        InitializeComponent(); this.project = project; this.existing = existing;
        var saved = existing?.Paths.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        roots = project.Roots.Select(r => new FolderNode(r.Path, saved)).ToList(); FolderTree.ItemsSource = roots;
        NameInput.Text = existing?.Name ?? project.Name + " — selected folders";
    }
    private void FolderExpanded(object sender, RoutedEventArgs e) { if (e.OriginalSource is TreeViewItem { DataContext: FolderNode node }) node.Load(); }
    private void SaveClick(object sender, RoutedEventArgs e)
    {
        var paths = roots.SelectMany(r => r.SelectedPaths()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count == 0 || string.IsNullOrWhiteSpace(NameInput.Text)) { MessageBox.Show(this, "Choose at least one folder or file and enter a selection name.", "Folder selection"); return; }
        Selection = new() { Id = existing?.Id ?? Guid.NewGuid().ToString("N"), ProjectId = project.Id, Name = NameInput.Text.Trim(), Paths = paths, Exclusions = existing?.Exclusions ?? project.Exclusions }; DialogResult = true;
    }
}
