using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using RepoBackup.Desktop.Views;

namespace RepoBackup.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private void InitializeProjectCommands()
    {
        RefreshCommand = Command(RefreshAsync);
        AddFolderCommand = Command(async () =>
        {
            var path = dialogs.Folder("Add a project or folder to back up");
            if (path is not null) await OperateAsync(async token =>
            {
                ProgressStage = "Adding folder…";
                await Task.Run(() => Services.Catalog.RegisterCandidate(PathSafety.DisplayName(path), [path], "Manual", approved: true), token);
                StatusMessage = "Folder added.";
            });
        });
        ScanCommand = Command(async () =>
        {
            var path = dialogs.Folder("Scan for Git repositories — results will need review"); if (path is null) return;
            await DiscoverAsync(async token => { var count = await Services.Discovery.ScanAsync(path, token); return new Core.Discovery.DiscoveryResult(count, count, 0, []); });
        });
        ApproveCommand = new Infrastructure.AsyncCommand(async p =>
        {
            var item = (ProjectItem)p!;
            await OperateAsync(async token =>
            {
                ProgressStage = "Including project…";
                await Task.Run(() => Services.Catalog.SaveProject(item.Project with { Reviewed = true, Enabled = true, Dismissed = false }), token);
                StatusMessage = item.Name + " included.";
            });
        }, dialogs.Error, () => CanEditCatalog && !DiscoveryLoad.IsLoading);
        DismissCommand = new Infrastructure.AsyncCommand(async p =>
        {
            var item = (ProjectItem)p!;
            await OperateAsync(async token =>
            {
                ProgressStage = "Dismissing project…";
                await Task.Run(() => Services.Catalog.SaveProject(item.Project with { Reviewed = true, Dismissed = true, Enabled = false }), token);
            });
        }, dialogs.Error, () => CanEditCatalog && !DiscoveryLoad.IsLoading);
        RelinkCommand = Command(async () =>
        {
            if (SelectedProject is null) return;
            var chooser = new RootPickerDialog(SelectedProject.Project.Roots) { Owner = dialogs.Owner };
            if (chooser.ShowDialog() != true || chooser.SelectedRoot is null) return;
            var path = dialogs.Folder("Locate the moved source folder"); if (path is not null) { Services.Catalog.RelinkRoot(SelectedProject.Id, chooser.SelectedRoot.Id, path); await ReloadAsync(); StatusMessage = "Source relinked; identity and history preserved."; }
            return;
        }, () => SelectedProject is not null);
        SaveSelectionCommand = Command(async () =>
        {
            if (SelectedProject is null) return;
            var dialog = new FolderSelectionDialog(SelectedProject.Project, ActiveSelection?.Selection) { Owner = dialogs.Owner };
            if (dialog.ShowDialog() == true && dialog.Selection is { } selected) { Services.Catalog.SaveSelection(selected); await ReloadAsync(); ActiveSelection = Selections.Single(s => s.Selection?.Id == selected.Id); StatusMessage = "Saved partial selection. Full-project recovery points are separate."; }
            return;
        }, () => SelectedProject is not null);
        EditExclusionsCommand = Command(async () =>
        {
            if (SelectedProject is null) return;
            var current = ActiveSelection?.Selection?.Exclusions ?? SelectedProject.Project.Exclusions;
            var dialog = new ExclusionsDialog(current) { Owner = dialogs.Owner };
            if (dialog.ShowDialog() == true)
            {
                if (ActiveSelection?.Selection is { } selection) Services.Catalog.SaveSelection(selection with { Exclusions = dialog.Rules });
                else Services.Catalog.SaveProject(SelectedProject.Project with { Exclusions = dialog.Rules });
                await ReloadAsync(); _ = UpdatePreviewAsync();
            }
            return;
        }, () => SelectedProject is not null);
    }
    public Func<CancellationToken, Task<Core.Discovery.DiscoveryResult>>? DiscoveryLoader { get; set; }
    public string DiscoveryWarningText { get; private set; } = "";
    public bool HasDiscoveryWarnings => DiscoveryWarningText.Length > 0;
    public Task RefreshDiscoveryAsync() => RefreshAsync();
    public void CancelDiscovery() => DiscoveryLoad.Cancel();
    private Task RefreshAsync() => DiscoverAsync(token => DiscoveryLoader?.Invoke(token) ?? Services.Discovery.RefreshAsync(token: token));
    private async Task DiscoverAsync(Func<CancellationToken, Task<Core.Discovery.DiscoveryResult>> discover)
    {
        using var request = DiscoveryLoad.Begin();
        DiscoveryWarningText = ""; Raise(nameof(DiscoveryWarningText)); Raise(nameof(HasDiscoveryWarnings));
        try
        {
            var result = await discover(request.Token);
            if (!DiscoveryLoad.Accepts(request)) return;
            DiscoveryWarningText = string.Join("\n", result.Warnings); Raise(nameof(DiscoveryWarningText)); Raise(nameof(HasDiscoveryWarnings));
            await ReloadAsync();
            if (!DiscoveryLoad.Accepts(request)) return;
            if (!CatalogLoad.IsReady) throw new InvalidOperationException("Discovery finished, but its results could not be loaded. Retry the catalog load.");
            StatusMessage = $"Discovery refreshed · {result.Added} new projects.";
            DiscoveryLoad.Complete(request);
        }
        catch (OperationCanceledException) { DiscoveryLoad.Cancel(request); }
        catch (Exception error) { DiscoveryLoad.Fail(request, error); }
    }
    private async Task UpdatePreviewAsync()
    {
        using var request = PreviewLoad.Begin();
        var project = SelectedProject?.Project; var selection = ActiveSelection?.Selection;
        preview = null; SourceTree.Clear(); UpdatePreviewProperties();
        if (project is null) { PreviewLoad.Cancel(request); return; }
        try
        {
            var loaded = await (PreviewLoader?.Invoke(project, selection, request.Token) ?? Task.Run(() => Services.Planner.PreviewAsync(project, selection, request.Token), request.Token));
            if (!PreviewLoad.Accepts(request)) return;
            var nodes = await Task.Run(() => PreviewSources.Build(loaded, selection is null), request.Token);
            if (!PreviewLoad.Accepts(request)) return;
            preview = loaded;
            foreach (var node in nodes) SourceTree.Add(node);
            UpdatePreviewProperties(); PreviewLoad.Complete(request);
        }
        catch (OperationCanceledException) { PreviewLoad.Cancel(request); }
        catch (Exception e) { PreviewLoad.Fail(request, e); }
    }
    public Task RefreshPreviewAsync() => UpdatePreviewAsync();
    private void UpdatePreviewProperties()
    {
        foreach (var property in new[] { nameof(PreviewTitle), nameof(PreviewFiles), nameof(PreviewSize), nameof(PreviewWarnings), nameof(HasPreviewWarnings), nameof(ExclusionText), nameof(ExclusionDetails), nameof(CoverageText) }) Raise(property);
    }
}
