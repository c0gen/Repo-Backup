using RepoBackup.Core.Models;
using RepoBackup.Desktop.Infrastructure;

namespace RepoBackup.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private CancellationTokenSource? operation;
    private TaskCompletionSource? operationFinished;
    private string stage = "", progressCurrentFile = "", progressSummary = "";
    private double fraction;
    public string ProgressStage { get => stage; private set => Set(ref stage, value); }
    public double ProgressPercent { get => fraction; private set => Set(ref fraction, value); }
    public bool IsProgressIndeterminate { get; private set; } = true;
    public string ProgressSummary { get => progressSummary; private set { if (Set(ref progressSummary, value)) Raise(nameof(HasProgressSummary)); } }
    public bool HasProgressSummary => ProgressSummary.Length > 0;
    public string ProgressCurrentFile { get => progressCurrentFile; private set { if (Set(ref progressCurrentFile, value)) { Raise(nameof(HasProgressCurrentFile)); Raise(nameof(ProgressFileLabel)); } } }
    public bool HasProgressCurrentFile => ProgressCurrentFile.Length > 0;
    public string ProgressFileLabel => "Latest restored file: " + ProgressCurrentFile;

    public void Cancel() => operation?.Cancel();
    private async Task OperateAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) throw new InvalidOperationException("Wait for the current operation to finish.");
        if (!Queue.IsQuiescent) throw new InvalidOperationException("Pause the backup queue and wait for its current work to finish first.");
        using var cancellation = new CancellationTokenSource();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        operationFinished = finished;
        operation = cancellation;
        ProgressStage = ProgressCurrentFile = ProgressSummary = "";
        ProgressPercent = 0;
        IsProgressIndeterminate = true;
        Raise(nameof(IsProgressIndeterminate));
        IsBusy = true;
        operations.CancelReads();
        try { using var lease = await operations.EnterAsync(cancellation.Token); await action(cancellation.Token); }
        catch (OperationCanceledException) { StatusMessage = "Operation cancelled."; throw; }
        finally
        {
            operation = null;
            ProgressCurrentFile = "";
            try { await ReloadAsync(); }
            finally { IsBusy = false; operationFinished = null; finished.TrySetResult(); }
        }
    }

    private void ApplyProgress(BackupProgress value, CancellationTokenSource? owner)
    {
        if (owner is null || !ReferenceEquals(owner, operation)) return;
        ProgressStage = value.ProjectName.Length == 0 ? value.Stage : value.ProjectName + " Â· " + value.Stage;
        ProgressPercent = double.IsFinite(value.Fraction) ? Math.Clamp(value.Fraction, 0, 1) * 100 : 0;
        IsProgressIndeterminate = value.IsIndeterminate;
        Raise(nameof(IsProgressIndeterminate));
        ProgressCurrentFile = value.CurrentFile ?? "";
        ProgressSummary = value.TotalBytes is long total
            ? (value.IsIndeterminate ? "" : $"{ProgressPercent:0}% Â· ") + $"{FormatBytes(value.Bytes)} of {FormatBytes(total)} " + (value.Stage.StartsWith("Backing up", StringComparison.Ordinal) ? "processed" : "restored")
            : "";
        StatusMessage = value.Stage;
    }

    private DispatcherProgress RestoreReporter()
    {
        var owner = operation;
        return new DispatcherProgress(p => ApplyProgress(p, owner));
    }
}
