using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using RepoBackup.Desktop.Views;

namespace RepoBackup.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private void InitializeProjectCommands()
    {
        RefreshCommand = Command(RefreshAsync);
        AddFolderCommand = Command(() =>
        {
            var path = dialogs.Folder("Add a project or folder to back up");
            if (path is not null) { Services.Catalog.RegisterCandidate(PathSafety.DisplayName(path), [path], "Manual", approved: true); Reload(); StatusMessage = "Folder added."; }
            return Task.CompletedTask;
        });
        ScanCommand = Command(async () =>
        {
            var path = dialogs.Folder("Scan for Git repositories — results will need review"); if (path is null) return;
            await OperateAsync(async token => { ProgressStage = "Scanning repositories"; var count = await Services.Discovery.ScanAsync(path, token); StatusMessage = $"Found {count} repositories for review."; });
        });
        ApproveCommand = new Infrastructure.RelayCommand(p => { var item = (ProjectItem)p!; Services.Catalog.SaveProject(item.Project with { Reviewed = true, Enabled = true, Dismissed = false }); Reload(); StatusMessage = item.Name + " included."; }, _ => IsIdle);
        DismissCommand = new Infrastructure.RelayCommand(p => { var item = (ProjectItem)p!; Services.Catalog.SaveProject(item.Project with { Reviewed = true, Dismissed = true, Enabled = false }); Reload(); }, _ => IsIdle);
        RelinkCommand = Command(() =>
        {
            if (SelectedProject is null) return Task.CompletedTask;
            var chooser = new RootPickerDialog(SelectedProject.Project.Roots) { Owner = dialogs.Owner };
            if (chooser.ShowDialog() != true || chooser.SelectedRoot is null) return Task.CompletedTask;
            var path = dialogs.Folder("Locate the moved source folder"); if (path is not null) { Services.Catalog.RelinkRoot(SelectedProject.Id, chooser.SelectedRoot.Id, path); Reload(); StatusMessage = "Source relinked; identity and history preserved."; }
            return Task.CompletedTask;
        }, () => SelectedProject is not null);
        SaveSelectionCommand = Command(() =>
        {
            if (SelectedProject is null) return Task.CompletedTask;
            var dialog = new FolderSelectionDialog(SelectedProject.Project, ActiveSelection?.Selection) { Owner = dialogs.Owner };
            if (dialog.ShowDialog() == true && dialog.Selection is { } selected) { Services.Catalog.SaveSelection(selected); Reload(); ActiveSelection = Selections.Single(s => s.Selection?.Id == selected.Id); StatusMessage = "Saved partial selection. Full-project recovery points are separate."; }
            return Task.CompletedTask;
        }, () => SelectedProject is not null);
        EditExclusionsCommand = Command(() =>
        {
            if (SelectedProject is null) return Task.CompletedTask;
            var current = ActiveSelection?.Selection?.Exclusions ?? SelectedProject.Project.Exclusions;
            var dialog = new ExclusionsDialog(current) { Owner = dialogs.Owner };
            if (dialog.ShowDialog() == true)
            {
                if (ActiveSelection?.Selection is { } selection) Services.Catalog.SaveSelection(selection with { Exclusions = dialog.Rules });
                else Services.Catalog.SaveProject(SelectedProject.Project with { Exclusions = dialog.Rules });
                Reload(); _ = UpdatePreviewAsync();
            }
            return Task.CompletedTask;
        }, () => SelectedProject is not null);
    }
    private async Task RefreshAsync()
    {
        await OperateAsync(async token =>
        {
            ProgressStage = "Refreshing projects from Codex, Claude Code and Antigravity";
            var result = await Services.Discovery.RefreshAsync(token: token);
            StatusMessage = result.Warnings.Count == 0 ? $"Discovery refreshed · {result.Added} new projects." : string.Join(" · ", result.Warnings);
        });
    }
    private async Task UpdatePreviewAsync()
    {
        previewCancellation?.Cancel(); var cancellation = new CancellationTokenSource(); previewCancellation = cancellation;
        var project = SelectedProject?.Project; var selection = ActiveSelection?.Selection;
        preview = null; SourceTree.Clear(); UpdatePreviewProperties();
        if (project is null) { previewCancellation = null; cancellation.Dispose(); return; }
        try
        {
            var loaded = await Services.Planner.PreviewAsync(project, selection, cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            preview = loaded;
            foreach (var source in loaded.Sources.Where(s => s.Kind != SourceKind.Worktree))
            {
                var children = new List<SourceNode>();
                if (selection is null && source.Kind != SourceKind.GitMetadata)
                {
                    children.Add(new("Working files", "Staged, unstaged & untracked", "\uE73E", []));
                    if (Directory.Exists(Path.Combine(source.OriginalPath, ".git")) || File.Exists(Path.Combine(source.OriginalPath, ".git"))) children.Add(new(".git · Git history", "Branches, index & local Git data", "\uE73E", []));
                    children.Add(new("Configuration & assets", ".env and ignored configuration included", "\uE73E", []));
                }
                SourceTree.Add(new(PathSafety.DisplayName(source.OriginalPath), source.OriginalPath, source.Kind == SourceKind.GitMetadata ? "\uE8F1" : "\uE8B7", children));
            }
            var worktrees = loaded.Sources.Where(s => s.Kind == SourceKind.Worktree).Select(s => new SourceNode(PathSafety.DisplayName(s.OriginalPath), s.OriginalPath, "\uE73E", [])).ToList();
            if (worktrees.Count > 0) SourceTree.Add(new("Worktrees", $"{worktrees.Count} included", "\uE8B7", worktrees));
            UpdatePreviewProperties();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!cancellation.IsCancellationRequested) StatusMessage = "Preview failed: " + e.Message; }
        finally { if (previewCancellation == cancellation) previewCancellation = null; cancellation.Dispose(); }
    }
    public Task RefreshPreviewAsync() => UpdatePreviewAsync();
    private void UpdatePreviewProperties()
    {
        foreach (var property in new[] { nameof(PreviewTitle), nameof(PreviewFiles), nameof(PreviewSize), nameof(PreviewWarnings), nameof(HasPreviewWarnings), nameof(ExclusionText), nameof(ExclusionDetails), nameof(CoverageText) }) Raise(property);
    }
}
