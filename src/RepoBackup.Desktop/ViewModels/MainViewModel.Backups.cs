using RepoBackup.Core.Models;
using RepoBackup.Core.Backup;

namespace RepoBackup.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    public Func<Infrastructure.DestinationInput, CancellationToken, Task<Destination>>? DestinationCreator { get; set; }
    private void InitializeBackupCommands()
    {
        BackupAllCommand = Command(() => BackupAsync(true), () => SelectedDestination is not null && Projects.Any(p => p.Enabled));
        BackupSelectedCommand = Command(() => BackupAsync(false), () => SelectedDestination is not null && (SelectedCount > 0 || ActiveSelection?.Selection is not null));
        AddDestinationCommand = Command(async () =>
        {
            var dialog = dialogs.Destination(); if (dialog is null) return;
            await AddDestinationAsync(dialog.Input);
        });
        VerifyCommand = Command(async () =>
        {
            var destination = SelectedDestination!;
            await OperateAsync(async token => { ProgressStage = "Verifying all repository data"; var result = await Services.Backups.VerifyAsync(destination, token); StatusMessage = result.Message; dialogs.Information(result.Message); });
        }, () => SelectedDestination is not null);
        ExportKeyCommand = Command(() => ExportRecoveryKeyAsync(SelectedDestination!), () => SelectedDestination?.Protection == DestinationProtection.RecoveryKey);
    }
    public async Task AddDestinationAsync(Infrastructure.DestinationInput input)
    {
            Destination? added = null;
            await OperateAsync(async token =>
            {
                ProgressStage = input.OpenExisting ? "Opening repository" : "Creating repository";
                added = await (DestinationCreator?.Invoke(input, token) ?? Task.Run(() => Services.Backups.AddDestinationAsync(input.Name, input.Path, input.RecoveryKey, input.OpenExisting, token, input.Protection), token));
                SelectedDestination = added;
                StatusMessage = added.Protection == DestinationProtection.PasswordFree
                    ? "Repository ready. No password or recovery key is needed to restore on another computer."
                    : "Repository ready. Export a recovery key and store it separately.";
            });
            if (!input.OpenExisting && added?.Protection == DestinationProtection.RecoveryKey) await ExportRecoveryKeyAsync(added);
    }
    private async Task ExportRecoveryKeyAsync(Destination destination)
    {
        var path = dialogs.Save("Export recovery key — store separately from the backup drive", "RepoBackup-recovery.key", "Recovery key|*.key");
        if (path is null) { StatusMessage = "Key export skipped. Export it later from Destinations before you need recovery on another computer."; return; }
        await Services.Backups.ExportRecoveryKeyAsync(destination, path, overwrite: true);
        StatusMessage = "Recovery key exported. Keep it somewhere safe and separate from the backup drive.";
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
            var successful = results.Count(RunOutcome.HasRecoveryPoint);
            StatusMessage = $"{successful}/{results.Count} projects backed up successfully" + (RunOutcome.ExitCode(results) == 130 ? " · Cancelled; review Activity." : RunOutcome.ExitCode(results) != 0 ? " · Review Activity for details." : ".");
        });
    }
}
