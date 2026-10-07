using RepoBackup.Core.Models;
using RepoBackup.Desktop.Infrastructure;

namespace RepoBackup.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private CancellationTokenSource? operation;
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
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        ProgressStage = ProgressCurrentFile = ProgressSummary = "";
        ProgressPercent = 0;
        IsProgressIndeterminate = true;
        Raise(nameof(IsProgressIndeterminate));
        IsBusy = true;
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { StatusMessage = "Operation cancelled."; throw; }
        finally
        {
            operation = null;
            ProgressCurrentFile = "";
            try { await ReloadAsync(); }
            finally { IsBusy = false; }
        }
    }

    private void ApplyProgress(BackupProgress value, CancellationTokenSource? owner)
    {
        if (owner is null || !ReferenceEquals(owner, operation)) return;
        ProgressStage = value.ProjectName.Length == 0 ? value.Stage : value.ProjectName + " · " + value.Stage;
        ProgressPercent = double.IsFinite(value.Fraction) ? Math.Clamp(value.Fraction, 0, 1) * 100 : 0;
        IsProgressIndeterminate = value.IsIndeterminate;
        Raise(nameof(IsProgressIndeterminate));
        ProgressCurrentFile = value.CurrentFile ?? "";
        ProgressSummary = value.TotalBytes is long total
            ? (value.IsIndeterminate ? "" : $"{ProgressPercent:0}% · ") + $"{FormatBytes(value.Bytes)} of {FormatBytes(total)} restored"
            : "";
        StatusMessage = value.Stage;
    }

    private IProgress<BackupProgress> Progress()
    {
        var owner = operation;
        return new Progress<BackupProgress>(p => ApplyProgress(p, owner));
    }

    private DispatcherProgress RestoreReporter()
    {
        var owner = operation;
        return new DispatcherProgress(p => ApplyProgress(p, owner));
    }
}
