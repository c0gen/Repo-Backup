using RepoBackup.Core.Models;

namespace RepoBackup.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    public Func<Destination, CancellationToken, Task<List<SnapshotInfo>>>? SnapshotLoader { get; set; }
    public Func<Destination, SnapshotInfo, CancellationToken, Task<List<SnapshotFileItem>>>? SnapshotFileLoader { get; set; }
    private void InitializeSnapshotCommands()
    {
        RefreshSnapshotsCommand = Command(RefreshSnapshotsAsync, () => SelectedDestination is not null);
        RestoreCommand = Command(() => RestoreAsync(false, false), () => SelectedDestination is not null && SelectedSnapshot is not null);
        RestoreFileCommand = Command(() => RestoreAsync(true, false), () => SelectedDestination is not null && SelectedSnapshot is not null && SelectedSnapshotFile is not null);
        TestRestoreCommand = Command(() => RestoreAsync(false, true), () => SelectedDestination is not null && SelectedSnapshot is not null);
        RecoverCatalogCommand = Command(async () =>
        {
            var destination = SelectedDestination!;
            await OperateAsync(async token => { ProgressStage = "Rebuilding catalog from recovery metadata"; var count = await Services.Restore.RebuildCatalogAsync(destination, token); StatusMessage = $"Recovered {count} project identities. Relink missing roots before enabling them."; });
        }, () => SelectedDestination is not null);
    }
    public async Task RefreshSnapshotsAsync()
    {
        var destination = SelectedDestination;
        using var request = SnapshotsLoad.Begin(DestinationIdentity);
        Snapshots.Clear(); SelectedSnapshot = null;
        if (destination is null) { SnapshotsLoad.Cancel(request); return; }
        try
        {
            List<SnapshotInfo> snapshots;
            if (SnapshotLoader is { } loader) snapshots = await loader(destination, request.Token);
            else
            {
                snapshots = await Task.Run(async () =>
                {
                    await Services.Restic.RepositoryIdentityAsync(destination, request.Token);
                    return await Services.Restic.SnapshotsAsync(destination, request.Token);
                }, request.Token);
            }
            if (!SnapshotsLoad.Accepts(request)) return;
            foreach (var snapshot in snapshots) Snapshots.Add(new(snapshot));
            StatusMessage = $"{snapshots.Count} recovery points loaded."; SelectedSnapshot = Snapshots.FirstOrDefault(); SnapshotsLoad.Complete(request);
        }
        catch (OperationCanceledException) { SnapshotsLoad.Cancel(request); }
        catch (Exception e) { SnapshotsLoad.Fail(request, e); }
    }
    private string FileRequestIdentity => DestinationIdentity + "|" + SelectedSnapshot?.Snapshot.Id;
    private async Task LoadSnapshotFilesAsync()
    {
        using var request = SnapshotFilesLoad.Begin(FileRequestIdentity);
        SnapshotFiles.Clear(); SelectedSnapshotFile = null; var destination = SelectedDestination; var snapshot = SelectedSnapshot;
        if (destination is null || snapshot is null) { SnapshotFilesLoad.Cancel(request); return; }
        try
        {
            List<SnapshotFileItem> files;
            if (SnapshotFileLoader is { } loader) files = await loader(destination, snapshot.Snapshot, request.Token);
            else
            {
                files = await Task.Run(async () =>
                {
                    var manifest = await Services.Restic.ManifestAsync(destination, snapshot.Snapshot, request.Token);
                    return SnapshotBrowser.ProjectFiles(await Services.Restic.FilesAsync(destination, snapshot.Snapshot.Id, request.Token), manifest).ToList();
                }, request.Token);
            }
            if (!SnapshotFilesLoad.Accepts(request)) return;
            foreach (var file in files) SnapshotFiles.Add(file);
            SnapshotFilesLoad.Complete(request);
        }
        catch (OperationCanceledException) { SnapshotFilesLoad.Cancel(request); }
        catch (Exception e) { SnapshotFilesLoad.Fail(request, e); }
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
