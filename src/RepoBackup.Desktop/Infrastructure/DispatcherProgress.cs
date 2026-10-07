using System.Windows.Threading;
using RepoBackup.Core.Models;

namespace RepoBackup.Desktop.Infrastructure;

// Keep only the newest routine update, rather than queuing one UI callback per file.
public sealed class DispatcherProgress : IProgress<BackupProgress>, IDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer timer;
    private readonly Action<BackupProgress> apply;
    private readonly object gate = new();
    private BackupProgress? latest;
    private long sequence, applied;
    private bool disposed;

    public DispatcherProgress(Action<BackupProgress> apply)
    {
        this.apply = apply;
        dispatcher = Dispatcher.CurrentDispatcher;
        timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += OnTick;
        timer.Start();
    }

    public void Report(BackupProgress value)
    {
        long version;
        bool immediate;
        lock (gate)
        {
            if (disposed) return;
            immediate = latest is null || latest.ProjectName != value.ProjectName || latest.Stage != value.Stage
                || latest.IsIndeterminate != value.IsIndeterminate || value.Fraction >= 1;
            latest = value;
            version = ++sequence;
        }
        if (immediate) dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => Apply(value, version)));
    }

    private void OnTick(object? sender, EventArgs e) => Flush();

    private void Apply(BackupProgress value, long version)
    {
        lock (gate)
        {
            if (disposed || version <= applied) return;
            applied = version;
        }
        apply(value);
    }

    private void Flush()
    {
        dispatcher.VerifyAccess();
        BackupProgress? value;
        long version;
        lock (gate) { value = latest; version = sequence; }
        if (value is not null) Apply(value, version);
    }

    // Called on the UI thread before displaying the terminal result.
    public void Complete()
    {
        Flush();
        Dispose();
    }

    public void Dispose()
    {
        dispatcher.VerifyAccess();
        lock (gate) { disposed = true; latest = null; }
        timer.Stop();
        timer.Tick -= OnTick;
    }
}
