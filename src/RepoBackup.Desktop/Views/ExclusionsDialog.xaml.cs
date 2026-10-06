using System.Windows;
using RepoBackup.Core.Models;

namespace RepoBackup.Desktop.Views;

public sealed class DirectoryRule(string name, bool excluded) { public string Name { get; } = name; public bool Excluded { get; set; } = excluded; }
public partial class ExclusionsDialog : Window
{
    private readonly List<DirectoryRule> directories;
    public ExclusionRules Rules { get; private set; }
    public ExclusionsDialog(ExclusionRules current)
    {
        InitializeComponent(); Rules = current;
        directories = ExclusionRules.Defaults.Concat(current.ExcludedDirectories).Distinct(StringComparer.OrdinalIgnoreCase).Select(d => new DirectoryRule(d, current.ExcludedDirectories.Contains(d, StringComparer.OrdinalIgnoreCase))).ToList();
        RulesList.ItemsSource = directories; PatternsInput.Text = string.Join("\n", current.RelativePatterns);
    }
    private void ApplyClick(object sender, RoutedEventArgs e) { Rules = new() { ExcludedDirectories = directories.Where(d => d.Excluded).Select(d => d.Name).ToList(), RelativePatterns = PatternsInput.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() }; DialogResult = true; }
}
