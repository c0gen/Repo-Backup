using RepoBackup.Core.Models;

namespace RepoBackup.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private CancellationTokenSource? filesCancellation;
    private void InitializeSnapshotCommands()
    {
        RefreshSnapshotsCommand = Command(async () =>
        {
            var destination = SelectedDestination!;
            await OperateAsync(async token =>
            {
                ProgressStage = "Loading recovery points"; await Services.Restic.RepositoryIdentityAsync(destination, token);
                var snapshots = await Services.Restic.SnapshotsAsync(destination, token); Snapshots.Clear(); foreach (var s in snapshots) Snapshots.Add(new(s));
                StatusMessage = $"{snapshots.Count} recovery points loaded."; SelectedSnapshot = Snapshots.FirstOrDefault();
            });
        }, () => SelectedDestination is not null);
        RestoreCommand = Command(() => RestoreAsync(false, false), () => SelectedDestination is not null && SelectedSnapshot is not null);
        RestoreFileCommand = Command(() => RestoreAsync(true, false), () => SelectedDestination is not null && SelectedSnapshot is not null && SelectedSnapshotFile is not null);
        TestRestoreCommand = Command(() => RestoreAsync(false, true), () => SelectedDestination is not null && SelectedSnapshot is not null);
        RecoverCatalogCommand = Command(async () =>
        {
            var destination = SelectedDestination!;
            await OperateAsync(async token => { ProgressStage = "Rebuilding catalog from recovery metadata"; var count = await Services.Restore.RebuildCatalogAsync(destination, token); StatusMessage = $"Recovered {count} project identities. Relink missing roots before enabling them."; });
        }, () => SelectedDestination is not null);
    }
    private async Task LoadSnapshotFilesAsync()
    {
        filesCancellation?.Cancel(); using var cancellation = new CancellationTokenSource(); filesCancellation = cancellation;
        SnapshotFiles.Clear(); SelectedSnapshotFile = null; var destination = SelectedDestination; var snapshot = SelectedSnapshot;
        if (destination is null || snapshot is null) { filesCancellation = null; return; }
        try
        {
            var manifest = await Services.Restic.ManifestAsync(destination, snapshot.Snapshot, cancellation.Token);
            var files = await Services.Restic.FilesAsync(destination, snapshot.Snapshot.Id, cancellation.Token);
            if (!cancellation.IsCancellationRequested) foreach (var file in SnapshotBrowser.ProjectFiles(files, manifest)) SnapshotFiles.Add(file);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!cancellation.IsCancellationRequested) StatusMessage = "Snapshot browser: " + e.Message; }
        finally { if (filesCancellation == cancellation) filesCancellation = null; }
    }
    public Task RefreshSnapshotFilesAsync() => LoadSnapshotFilesAsync();
    private async Task RestoreAsync(bool selectedPath, bool test)
    {
        var destination = SelectedDestination!; var snapshot = SelectedSnapshot!.Snapshot; var file = selectedPath ? SelectedSnapshotFile?.Path : null;
        var parent = dialogs.Folder(test ? "Choose a parent folder for a verified test restore" : "Choose a parent folder — restore creates a new child directory"); if (parent is null) return;
        var target = Path.Combine(parent, (test ? "TestRestore-" : "Restored-") + snapshot.Id[..8] + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..4]);
        await OperateAsync(async token =>
        {
            ProgressStage = "Restoring and verifying file data";
            var restored = await Services.Restore.RestoreAsync(destination, snapshot, target, file, Progress(), token);
            StatusMessage = (test ? "Test restore passed: " : "Restore completed: ") + restored;
            dialogs.Information(StatusMessage);
        });
    }
}
