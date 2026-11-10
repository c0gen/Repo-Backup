using RepoBackup.Core.Models;
using RepoBackup.Desktop.Infrastructure;

namespace RepoBackup.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    public Func<Destination, CancellationToken, Task<List<SnapshotSummary>>>? SnapshotLoader { get; set; }
    public Func<Destination, SnapshotSummary, CancellationToken, Task<List<SnapshotFileItem>>>? SnapshotFileLoader { get; set; }
    public Func<Destination, SnapshotSummary, string, string?, IProgress<BackupProgress>, CancellationToken, Task<string>>? RestoreExecutor { get; set; }
    private void InitializeBrowser()
    {
        Browser.SummaryLoader = (destination, token) => operations.ReadAsync(async cancellation =>
        {
            if (SnapshotLoader is { } loader) return await loader(destination, cancellation);
            return await Task.Run(async () =>
            {
                await Services.Restic.RepositoryIdentityAsync(destination, cancellation);
                return await Services.Restic.SnapshotSummariesAsync(destination, cancellation);
            }, cancellation);
        }, () => Queue.IsQuiescent && IsIdle, token);
        Browser.FileLoader = (destination, snapshot, token) => operations.ReadAsync(async cancellation =>
        {
            if (SnapshotFileLoader is { } loader) return await loader(destination, snapshot, cancellation);
            return await Task.Run(async () =>
            {
                var manifest = await Services.Restic.ManifestAsync(destination, snapshot, cancellation);
                var files = await Services.Restic.FilesAsync(destination, snapshot.Id, cancellation);
                return SnapshotBrowser.ProjectFiles(Core.Recovery.SnapshotContents.Merge(manifest, files), manifest).ToList();
            }, cancellation);
        }, () => Queue.IsQuiescent && IsIdle, token);
        Browser.CanonicalSeries = series => series.StartsWith("project-", StringComparison.Ordinal)
            ? "project-" + (Projects.FirstOrDefault(p => p.Id == series[8..] || p.Project.LegacyIds.Contains(series[8..]))?.Id ?? series[8..]) : series;
        Browser.SeriesLabel = series => series.StartsWith("project-", StringComparison.Ordinal)
            ? Projects.FirstOrDefault(p => "project-" + p.Id == series)?.Name
            : Selections.FirstOrDefault(s => "selection-" + s.Selection?.Id == series)?.Name;
        Browser.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Browser.SelectedSnapshot)) Raise(nameof(SelectedSnapshot));
            if (e.PropertyName == nameof(Browser.SelectedFile)) Raise(nameof(SelectedSnapshotFile));
            if (e.PropertyName == nameof(Browser.Search)) Raise(nameof(FileSearch));
            RaiseLoadProperties();
        };
    }
    private void InitializeSnapshotCommands()
    {
        RefreshSnapshotsCommand = ExclusiveCommand(RefreshSnapshotsAsync, () => SelectedDestination is { IsRetired: false });
        RestoreCommand = ExclusiveCommand(() => RestoreAsync(false, false), () => SelectedDestination is { IsRetired: false } && SelectedSnapshot is not null);
        RestoreFileCommand = ExclusiveCommand(() => RestoreAsync(true, false), () => SelectedDestination is { IsRetired: false } && SelectedSnapshot is not null && SelectedSnapshotFile is not null);
        TestRestoreCommand = ExclusiveCommand(() => RestoreAsync(false, true), () => SelectedDestination is { IsRetired: false } && SelectedSnapshot is not null);
        RecoverCatalogCommand = ExclusiveCommand(async () =>
        {
            var destination = SelectedDestination!;
            await OperateAsync(async token => { ProgressStage = "Rebuilding catalog from recovery metadata"; var count = await Services.Restore.RebuildCatalogAsync(destination, token); StatusMessage = $"Recovered {count} project identities. Relink missing roots before enabling them."; });
        }, () => SelectedDestination is { IsRetired: false });
    }
    public Task RefreshSnapshotsAsync() => Browser.RefreshAsync();
    private Task LoadSnapshotFilesAsync() => Browser.LoadFilesAsync();
    public Task RefreshSnapshotFilesAsync() => Browser.LoadFilesAsync();
    private async Task RestoreAsync(bool selectedPath, bool test)
    {
        var destination = SelectedDestination!; var snapshot = SelectedSnapshot!.Snapshot; var file = selectedPath ? SelectedSnapshotFile?.Path : null;
        var parent = dialogs.Folder(test ? "Choose a parent folder for a verified test restore" : "Choose a parent folder — restore creates a new child directory"); if (parent is null) return;
        var target = Path.Combine(parent, (test ? "TestRestore-" : "Restored-") + snapshot.Id[..8] + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..4]);
        await OperateAsync(async token =>
        {
            ProgressStage = "Preparing restore";
            using var reporter = RestoreReporter();
            var executor = RestoreExecutor;
            var restored = await Task.Run(() => executor is null
                ? Services.Restore.RestoreAsync(destination, snapshot, target, file, reporter, token)
                : executor(destination, snapshot, target, file, reporter, token), token);
            reporter.Complete();
            StatusMessage = (test ? "Test restore passed: " : "Restore completed: ") + restored;
            dialogs.Information(StatusMessage);
        });
    }
}
