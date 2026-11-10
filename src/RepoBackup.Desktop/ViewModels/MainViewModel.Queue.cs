using RepoBackup.Core.Backup;
using RepoBackup.Core.Models;

namespace RepoBackup.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    public BackupQueueViewModel Queue { get; }
    private long historyRequest;
    private bool isClosing;
    public bool IsClosing { get => isClosing; private set { if (Set(ref isClosing, value)) RaiseLoadProperties(); } }
    public bool RepositoryBrowsingDeferred => !Queue.IsQuiescent;

    private void InitializeQueue()
    {
        Queue.StateChanged += () =>
        {
            RaiseLoadProperties(); Raise(nameof(SelectedBackupLabel)); Raise(nameof(BackupAllLabel)); Raise(nameof(RepositoryBrowsingDeferred));
            Browser.RepositoryStateChanged();
            if (Queue.IsQuiescent && IsSnapshots && !IsClosing) _ = Browser.ActivateAsync();
        };
        Queue.ResultsChanged += results => _ = RefreshBackupHistoryAsync(results);
        Queue.Faulted += dialogs.Error;
    }

    private async Task RefreshBackupHistoryAsync(IReadOnlyList<JobRecord> results)
    {
        var request = ++historyRequest;
        try
        {
            Browser.Invalidate(results.Select(r => r.DestinationId));
            var history = await Task.Run(Services.Catalog.History);
            if (request != historyRequest || IsClosing) return;
            var selectedId = SelectedActivity?.Id;
            Activity.Clear(); foreach (var job in history) Activity.Add(job);
            SelectedActivity = Activity.FirstOrDefault(j => j.Id == selectedId);
            RefreshProtection();
            if (Queue.IsQuiescent && IsSnapshots && !IsClosing) await Browser.ActivateAsync();
            foreach (var property in new[] { nameof(LastRun), nameof(HasLastRun), nameof(LastRunTime) }) Raise(property);
            if (!IsBusy)
                StatusMessage = $"{results.Count(RunOutcome.HasRecoveryPoint)}/{results.Count} projects backed up successfully"
                    + (RunOutcome.ExitCode(results) != 0 ? " Â· Review Activity for details." : ".");
        }
        catch (Exception error) { StatusMessage = "Could not refresh Activity: " + error.Message; }
    }

    public async Task<bool> PrepareToCloseAsync()
    {
        if (IsBusy || Queue.HasUnfinishedWork)
        {
            if (!dialogs.Confirm("Backups or other operations are unfinished. Cancel active work, discard waiting backups, and close when it is safe?")) return false;
            IsClosing = true;
            Queue.CancelAll(); Cancel();
            await Queue.WaitForStopAsync();
            if (operationFinished is { } finished) await finished.Task;
        }
        StopBackgroundReads();
        return true;
    }
}
