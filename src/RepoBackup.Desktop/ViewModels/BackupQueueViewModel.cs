using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using RepoBackup.Core.Backup;
using RepoBackup.Core.Models;
using RepoBackup.Desktop.Infrastructure;

namespace RepoBackup.Desktop.ViewModels;

public sealed class BackupQueueViewModel : ObservableObject, IDisposable
{
    private readonly BackupQueueCoordinator coordinator;
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer timer;
    private readonly Stopwatch elapsed = new();
    private readonly Func<bool> canRun;
    private volatile DispatcherProgress? reporter;
    private BackupQueueSnapshot snapshot = new(-1, [], false, false, false, null, null, false);
    private bool disposed;
    public ObservableCollection<BackupQueueItem> Items { get; } = [];
    public event Action? StateChanged;
    public event Action<IReadOnlyList<JobRecord>>? ResultsChanged;
    public event Action<Exception>? Faulted;
    public bool IsRunning => snapshot.IsRunning;
    public bool IsPaused => snapshot.IsPaused;
    public bool IsQuiescent => !coordinator.Snapshot.IsRunning;
    public bool HasWork => snapshot.IsRunning || Items.Count > 0;
    public bool HasUnfinishedWork => coordinator.Snapshot is { } state && (state.IsRunning || state.Entries.Count > 0);
    public int WaitingCount => Items.Count(i => i.IsWaiting);
    public string Summary => (IsPaused ? IsRunning ? "Pausing after current project" : "Queue paused" : IsRunning ? "Backup in progress" : "Backup queue") + $" · {WaitingCount} waiting";
    public string PauseLabel => IsPaused ? "Resume" : "Pause";
    public string CurrentProject => snapshot.CurrentRequestId is { } id ? Items.FirstOrDefault(i => i.Id == id)?.Name ?? "Preparing backup" : snapshot.IsFinalizing ? "Verifying and finalizing backups" : IsPaused ? "Queue paused" : "Backup queue";
    private string destination = "";
    public string CurrentDestination { get => destination; private set => Set(ref destination, value); }
    public string Elapsed => "Elapsed " + elapsed.Elapsed.ToString(elapsed.Elapsed.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");
    private string stage = "", summary = "", currentFile = "";
    private double percent;
    private bool indeterminate = true;
    public string ProgressStage { get => stage; private set => Set(ref stage, value); }
    public string ProgressSummary { get => summary; private set => Set(ref summary, value); }
    public double ProgressPercent { get => percent; private set => Set(ref percent, value); }
    public bool IsProgressIndeterminate { get => indeterminate; private set => Set(ref indeterminate, value); }
    public string ProgressCurrentFile
    {
        get => currentFile;
        private set { if (Set(ref currentFile, value)) { Raise(nameof(HasProgressCurrentFile)); Raise(nameof(ProgressFileLabel)); } }
    }
    public bool HasProgressCurrentFile => ProgressCurrentFile.Length > 0;
    public string ProgressFileLabel => "Current file: " + ProgressCurrentFile;
    public ICommand PauseResumeCommand { get; }
    public ICommand CancelCurrentCommand { get; }
    public ICommand CancelAllCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public bool CanCancelCurrent => snapshot.CanCancelCurrent;

    public BackupQueueViewModel(IBackupQueueBackend backend, Func<bool> canRun)
    {
        this.canRun = canRun;
        coordinator = new(backend);
        timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += Tick;
        coordinator.Changed += state => dispatcher.BeginInvoke(new Action(() => Apply(state)));
        coordinator.ProgressReported += progress => { if (coordinator.Accepts(progress)) reporter?.Report(progress); };
        coordinator.ResultsChanged += results => dispatcher.BeginInvoke(new Action(() => { if (!disposed) ResultsChanged?.Invoke(results); }));
        coordinator.Faulted += error => dispatcher.BeginInvoke(new Action(() => { if (!disposed) Faulted?.Invoke(error); }));
        PauseResumeCommand = new RelayCommand(_ =>
        {
            EnsureReporter();
            if (IsPaused) coordinator.Resume(); else coordinator.Pause();
            Apply(coordinator.Snapshot);
        }, _ => HasWork && (!IsPaused || this.canRun()));
        CancelCurrentCommand = new RelayCommand(_ =>
        {
            if (snapshot.CurrentRequestId is { } id) coordinator.CancelCurrent(id);
            Apply(coordinator.Snapshot);
        }, _ => CanCancelCurrent);
        CancelAllCommand = new RelayCommand(_ => CancelAll(), _ => HasUnfinishedWork);
        RemoveCommand = new RelayCommand(p => { if (p is BackupQueueItem item) { coordinator.Remove(item.Id); Apply(coordinator.Snapshot); } }, p => p is BackupQueueItem { IsWaiting: true });
        MoveUpCommand = MoveCommand(-1);
        MoveDownCommand = MoveCommand(1);
    }

    public (int Added, int Duplicates) Enqueue(IEnumerable<BackupRequest> requests)
    {
        dispatcher.VerifyAccess();
        if (!canRun()) throw new InvalidOperationException("Wait for the current operation to finish before adding backups.");
        EnsureReporter();
        var result = coordinator.Enqueue(requests);
        Apply(coordinator.Snapshot);
        return result;
    }

    public void CancelAll() { coordinator.CancelAll(); Apply(coordinator.Snapshot); }
    public async Task WaitForStopAsync()
    {
        await coordinator.WaitForStopAsync();
        Apply(coordinator.Snapshot);
    }
    public void Pause() { coordinator.Pause(); Apply(coordinator.Snapshot); }
    public void Resume()
    {
        if (!canRun()) return;
        EnsureReporter(); coordinator.Resume(); Apply(coordinator.Snapshot);
    }

    private ICommand MoveCommand(int direction) => new RelayCommand(p =>
    {
        if (p is BackupQueueItem item) { coordinator.Move(item.Id, direction); Apply(coordinator.Snapshot); }
    }, p => p is BackupQueueItem item && coordinator.CanMove(item.Id, direction));

    private void EnsureReporter() => reporter ??= new DispatcherProgress(ApplyProgress);
    private void Tick(object? sender, EventArgs e) => Raise(nameof(Elapsed));

    private void Apply(BackupQueueSnapshot state)
    {
        if (disposed || state.Version <= snapshot.Version) return;
        var wasRunning = IsRunning;
        var changedOwner = state.CurrentRequestId != snapshot.CurrentRequestId || state.RunId != snapshot.RunId;
        snapshot = state;
        var wanted = state.Entries.Select(e => e.Request.Id).ToHashSet();
        for (var i = Items.Count - 1; i >= 0; i--) if (!wanted.Contains(Items[i].Id)) Items.RemoveAt(i);
        for (var i = 0; i < state.Entries.Count; i++)
        {
            var entry = state.Entries[i];
            var item = Items.FirstOrDefault(x => x.Id == entry.Request.Id);
            if (item is null) { item = new(entry.Request); Items.Insert(i, item); }
            else if (Items.IndexOf(item) != i) Items.Move(Items.IndexOf(item), i);
            item.State = entry.State;
        }
        if (IsRunning && !wasRunning) { elapsed.Restart(); timer.Start(); EnsureReporter(); }
        if (!IsRunning)
        {
            elapsed.Stop(); timer.Stop();
            reporter?.Complete(); reporter = null;
        }
        if (changedOwner || !IsRunning)
        {
            ProgressStage = state.IsFinalizing ? "Verifying and finalizing backups" : IsPaused && !IsRunning ? "Paused" : "Preparing backup";
            ProgressSummary = ProgressCurrentFile = ""; ProgressPercent = 0; IsProgressIndeterminate = true;
        }
        var active = state.Entries.FirstOrDefault(e => e.Request.Id == state.CurrentRequestId) ?? state.Entries.FirstOrDefault();
        if (active is not null) CurrentDestination = active.Request.Destination.Name;
        foreach (var property in new[] { nameof(IsRunning), nameof(IsPaused), nameof(IsQuiescent), nameof(HasWork), nameof(HasUnfinishedWork),
            nameof(WaitingCount), nameof(Summary), nameof(PauseLabel), nameof(CurrentProject), nameof(CanCancelCurrent), nameof(Elapsed) }) Raise(property);
        CommandManager.InvalidateRequerySuggested();
        StateChanged?.Invoke();
    }

    private void ApplyProgress(BackupProgress progress)
    {
        if (disposed || !coordinator.Accepts(progress)) return;
        ProgressStage = progress.Stage;
        ProgressPercent = double.IsFinite(progress.Fraction) ? Math.Clamp(progress.Fraction, 0, 1) * 100 : 0;
        IsProgressIndeterminate = progress.IsIndeterminate;
        ProgressCurrentFile = progress.CurrentFile ?? "";
        var files = progress.TotalFiles is { } totalFiles ? $"{progress.Files:N0} / {totalFiles:N0} files" : $"{progress.Files:N0} files checked";
        ProgressSummary = progress.TotalBytes is { } total
            ? (progress.IsIndeterminate ? "" : $"{ProgressPercent:0}% · ") + files + $" · {MainViewModel.FormatBytes(progress.Bytes)} / {MainViewModel.FormatBytes(total)} processed"
            : progress.Files > 0 ? files + " · " + MainViewModel.FormatBytes(progress.Bytes) : "";
    }

    public void Dispose()
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        disposed = true; timer.Stop(); timer.Tick -= Tick;
        reporter?.Dispose(); reporter = null;
    }
}
