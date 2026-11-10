using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using RepoBackup.Core.Models;
using RepoBackup.Desktop.Infrastructure;

namespace RepoBackup.Desktop.Views;

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
        try { Selection = BuildSelection(); DialogResult = true; }
        catch (ArgumentException error) { MessageBox.Show(this, error.Message, "Folder selection"); }
    }
    public SavedSelection BuildSelection()
    {
        var paths = roots.SelectMany(r => r.SelectedPaths()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count == 0 || string.IsNullOrWhiteSpace(NameInput.Text)) throw new ArgumentException("Choose at least one folder or file and enter a selection name.");
        return existing is null ? SavedSelection.FromProject(project, NameInput.Text.Trim(), paths)
            : existing with { Name = NameInput.Text.Trim(), Paths = paths };
    }
}
