using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RepoBackup.Core.Models;
using RepoBackup.Desktop.ViewModels;
using RepoBackup.Desktop.Views;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

// Runs on the existing desktop fixture dispatcher and theme.
public static class SelectionDesktopRegressionTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        Directory.CreateDirectory(root);
        await tests.Run("Selection expansion, retries and explicit removal preserve unresolved saved paths", () =>
        {
            var source = Path.Combine(root, "selection"); Directory.CreateDirectory(source);
            var keep = Path.Combine(source, "z-keep.txt"); var missing = Path.Combine(source, "missing.txt"); File.WriteAllText(keep, "keep");
            WslLinkFixtures.Create(Path.Combine(source, "a-unsupported-link"), "dangling", 3);
            var saved = new HashSet<string>([keep, missing], StringComparer.OrdinalIgnoreCase);
            var node = new FolderNode(source, saved); node.Load();
            Assert(node.SelectedPaths().ToHashSet().SetEquals([keep, missing]), "Expansion removed saved paths.");
            Assert(node.Children.Any(c => c.Path == keep) && node.Children.Count(c => c.HasError) == 2, "One bad entry hid valid siblings or error states.");
            node.Load(true); Assert(node.SelectedPaths().ToHashSet().SetEquals([keep, missing]), "Retry removed a saved path.");
            var missingNode = node.Children.Single(c => c.Path == missing); missingNode.Included = false;
            Assert(node.SelectedPaths().SequenceEqual([keep]), "Explicit removal did not update saved selection.");
            node.Included = true; node.Children.Single(c => c.Path == keep).Included = false;
            Assert(!node.SelectedPaths().Contains(source) && !node.SelectedPaths().Contains(keep), "Unchecking a child retained a whole-folder selection.");
            var project = new ProjectEntry { Name = "Selection", Roots = [new("root", source)] };
            var existing = new SavedSelection { ProjectId = project.Id, Name = "Preserve unresolved files", Paths = [keep, missing] };
            var dialog = new FolderSelectionDialog(project, existing);
            try
            {
                var tree = (TreeView)dialog.FindName("FolderTree"); var branch = (FolderNode)tree.Items[0]; branch.Load();
                ShowOffscreen(dialog);
                if (tree.ItemContainerGenerator.ContainerFromItem(branch) is TreeViewItem row) row.IsExpanded = true;
                dialog.UpdateLayout(); Render(dialog, Path.Combine(root, "selection-errors-1.png"), 1); Render(dialog, Path.Combine(root, "selection-errors-1.5.png"), 1.5);
                Assert(dialog.BuildSelection().Paths.ToHashSet().SetEquals([keep, missing]), "Saving an expanded tree lost unresolved paths.");
            }
            finally { dialog.Close(); }
            return Task.CompletedTask;
        });
        await tests.Run("Legacy selection rules show their base and explicitly convert to project exclusions", () =>
        {
            var projectRules = new ExclusionRules { RelativePatterns = ["assets/private.txt"] };
            var dialog = new ExclusionsDialog(new() { RelativePatterns = ["private.txt"] }, ExclusionPatternBase.SelectedSource, projectRules);
            try
            {
                ShowOffscreen(dialog);
                Assert(((TextBlock)dialog.FindName("PatternBaseText")).Text.Contains("Legacy"), "Legacy pattern origin was hidden.");
                dialog.UseProjectRules();
                Assert(dialog.PatternBase == ExclusionPatternBase.ProjectRoot && ((TextBox)dialog.FindName("PatternsInput")).Text == "assets/private.txt", "Conversion did not copy project rules.");
                Render(dialog, Path.Combine(root, "project-exclusions-1.5.png"), 1.5);
                var apply = DesktopTestSupport.Descendants<Button>(dialog).Single(b => Equals(b.Content, "Apply rules"));
                Assert(DesktopTestSupport.Inside(apply, (FrameworkElement)dialog.Content), "Apply rules is clipped.");
            }
            finally { dialog.Close(); }
            return Task.CompletedTask;
        });
        await tests.Run("Retired destinations retain history and key export while repository actions are disabled", async () =>
        {
            var model = DesktopTestSupport.Model(Path.Combine(root, "retired"), out _);
            var previous = DesktopTestSupport.Destination(model, root, "Previous") with { Protection = DestinationProtection.RecoveryKey };
            model.Services.Catalog.SaveDestination(previous);
            var project = DesktopTestSupport.Project(model, Path.Combine(root, "retired"), "Protected project", approved: true);
            var replacement = previous with { Id = Guid.NewGuid().ToString("N"), Name = "Replacement" };
            var history = new JobRecord { ProjectId = project.Id, ProjectName = "Old history", DestinationId = previous.Id, SeriesId = "project-" + project.Id, FinishedAt = DateTimeOffset.UtcNow, Backup = Outcome.Successful, Verification = Outcome.Successful };
            model.Services.Catalog.SaveJob(history);
            var schedule = new ScheduleDefinition { DestinationId = previous.Id, Enabled = true }; model.Services.Catalog.SaveSchedules([schedule]);
            await model.InitializeAsync(discover: false, showSetup: false);
            model.SelectedSchedule = model.Schedules.Single();
            Assert(model.ScheduleEditor.Enabled, "Fixture did not have an enabled editing draft.");
            model.Services.Catalog.ReplaceDestination(previous, replacement);
            await model.ReloadAsync();
            Assert(!model.ScheduleEditor.Enabled, "Retirement left the old editing draft enabled.");
            Assert(model.SelectedDestination?.Id == replacement.Id && model.Projects.Single().NeedsBackup, "Replacement reused the old protection status.");
            model.Projects.Single().IsSelected = true;
            var retired = model.Destinations.Single(d => d.Id == previous.Id); model.SelectedDestination = retired;
            Assert(retired.DisplayName.Contains("history only") && model.Activity.Single().DestinationId == retired.Id, "Retired history or status was hidden.");
            Assert(!model.BackupAllCommand.CanExecute(null) && !model.BackupSelectedCommand.CanExecute(null) && !model.VerifyCommand.CanExecute(null) && !model.RecoverCatalogCommand.CanExecute(null) && !model.RefreshSnapshotsCommand.CanExecute(null) && model.ExportKeyCommand.CanExecute(null), "Retired destination allowed repository operations or hid key export.");
            Assert(model.ScheduleDestinations.All(d => !d.IsRetired) && !model.Schedules.Single().Enabled, "Retired destination remains a schedule choice.");
            model.SelectedSchedule = model.Schedules.Single();
            Assert(!model.SaveScheduleCommand.CanExecute(null), "A retired schedule could be saved without reassignment.");
            await DesktopTestSupport.WithWindowAsync(model, async window =>
            {
                model.Section = "Destinations"; DesktopTestSupport.Render(window, Path.Combine(root, "retired-destinations-1.png"));
                DesktopTestSupport.Render(window, Path.Combine(root, "retired-destinations-1.5.png"), 1.5);
                model.Section = "Settings"; await model.RefreshSchedulesAsync(); DesktopTestSupport.Render(window, Path.Combine(root, "retired-schedule-1.5.png"), 1.5);
            });
        });
    }

    private static void ShowOffscreen(Window window)
    {
        window.ShowInTaskbar = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -20000; window.Top = -20000; window.Show(); window.UpdateLayout();
    }
    private static void Render(Window window, string file, double scale)
    {
        window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)(window.ActualWidth * scale), (int)(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render((Visual)window.Content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(file); encoder.Save(output);
    }
}
