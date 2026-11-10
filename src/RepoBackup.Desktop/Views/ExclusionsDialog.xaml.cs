using System.Windows;
using RepoBackup.Core.Models;

namespace RepoBackup.Desktop.Views;

public sealed class DirectoryRule(string name, bool excluded) { public string Name { get; } = name; public bool Excluded { get; set; } = excluded; }
public partial class ExclusionsDialog : Window
{
    private readonly List<DirectoryRule> directories;
    private readonly ExclusionRules? projectRules;
    public ExclusionRules Rules { get; private set; }
    public ExclusionPatternBase PatternBase { get; private set; }
    public ExclusionsDialog(ExclusionRules current, ExclusionPatternBase patternBase = ExclusionPatternBase.ProjectRoot, ExclusionRules? projectRules = null)
    {
        InitializeComponent(); Rules = current; PatternBase = patternBase; this.projectRules = projectRules;
        directories = ExclusionRules.Defaults.Concat(current.ExcludedDirectories).Distinct(StringComparer.OrdinalIgnoreCase).Select(d => new DirectoryRule(d, current.ExcludedDirectories.Contains(d, StringComparer.OrdinalIgnoreCase))).ToList();
        RulesList.ItemsSource = directories; PatternsInput.Text = string.Join("\n", current.RelativePatterns);
        UseProjectRulesButton.Visibility = projectRules is null ? Visibility.Collapsed : Visibility.Visible;
        UpdatePatternBase();
    }
    private void UpdatePatternBase() => PatternBaseText.Text = PatternBase == ExclusionPatternBase.ProjectRoot
        ? "Patterns are relative to each original project root."
        : "Legacy selection: patterns are relative to each selected source.";
    public void UseProjectRules()
    {
        if (projectRules is null) return;
        Rules = projectRules; PatternBase = ExclusionPatternBase.ProjectRoot;
        directories.Clear();
        directories.AddRange(ExclusionRules.Defaults.Concat(projectRules.ExcludedDirectories).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(d => new DirectoryRule(d, projectRules.ExcludedDirectories.Contains(d, StringComparer.OrdinalIgnoreCase))));
        RulesList.Items.Refresh(); PatternsInput.Text = string.Join("\n", projectRules.RelativePatterns); UpdatePatternBase();
    }
    private void UseProjectRulesClick(object sender, RoutedEventArgs e) => UseProjectRules();
    private void ApplyClick(object sender, RoutedEventArgs e) { Rules = new() { ExcludedDirectories = directories.Where(d => d.Excluded).Select(d => d.Name).ToList(), RelativePatterns = PatternsInput.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() }; DialogResult = true; }
}
