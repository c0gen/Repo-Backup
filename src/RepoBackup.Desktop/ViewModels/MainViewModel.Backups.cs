using RepoBackup.Core.Models;
using RepoBackup.Desktop.Views;

namespace RepoBackup.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private void InitializeBackupCommands()
    {
        BackupAllCommand = Command(() => BackupAsync(true), () => SelectedDestination is not null && Projects.Any(p => p.Enabled));
        BackupSelectedCommand = Command(() => BackupAsync(false), () => SelectedDestination is not null && (SelectedCount > 0 || ActiveSelection?.Selection is not null));
        AddDestinationCommand = Command(async () =>
        {
            var dialog = new DestinationDialog { Owner = dialogs.Owner }; if (dialog.ShowDialog() != true) return;
            await OperateAsync(async token =>
            {
                ProgressStage = dialog.OpenExisting ? "Opening repository" : "Creating repository";
                var added = await Services.Backups.AddDestinationAsync(dialog.DestinationName, dialog.DestinationPath, dialog.RecoveryKey, dialog.OpenExisting, token);
                Services.Catalog.SaveSetting("selectedDestination", added.Id);
                StatusMessage = "Repository ready. Export a recovery key and store it separately.";
            });
        });
        VerifyCommand = Command(async () =>
        {
            var destination = SelectedDestination!;
            await OperateAsync(async token => { ProgressStage = "Verifying all repository data"; var result = await Services.Backups.VerifyAsync(destination, token); StatusMessage = result.Message; dialogs.Information(result.Message); });
        }, () => SelectedDestination is not null);
        ExportKeyCommand = Command(async () =>
        {
            var path = dialogs.Save("Export recovery key — store separately from the backup drive", "RepoBackup-recovery.key", "Recovery key|*.key");
            if (path is not null) { await File.WriteAllTextAsync(path, Services.Credentials.Get(SelectedDestination!.Id)); StatusMessage = "Recovery key exported. Keep it somewhere safe and separate from the backup drive."; }
        }, () => SelectedDestination is not null);
    }
    private async Task BackupAsync(bool all)
    {
        var destination = SelectedDestination!;
        var selection = all ? null : ActiveSelection?.Selection;
        var projects = selection is not null ? Projects.Where(p => p.Id == selection.ProjectId).Select(p => p.Project).ToList()
            : Projects.Where(p => all ? p.Enabled : p.IsSelected).Select(p => p.Project).ToList();
        await OperateAsync(async token =>
        {
            var results = await Services.Backups.RunAsync(destination, projects, selection, Progress(), token, UseVss);
            var successful = results.Count(j => j.Backup == Outcome.Successful && j.Verification == Outcome.Successful);
            StatusMessage = $"{successful}/{results.Count} projects backed up successfully" + (results.Any(j => j.Backup != Outcome.Successful || j.Verification != Outcome.Successful || j.Cleanup == Outcome.Failed) ? " · Review Activity for details." : ".");
        });
    }
}
