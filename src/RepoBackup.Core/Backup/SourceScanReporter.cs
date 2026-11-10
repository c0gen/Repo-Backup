using System.Diagnostics;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Backup;

internal sealed class SourceScanReporter(IProgress<SourceScanProgress>? progress)
{
    private long lastReport;
    public void Report(string path, long files, long bytes, bool force = false)
    {
        if (progress is null) return;
        var now = Stopwatch.GetTimestamp();
        if (!force && lastReport != 0 && Stopwatch.GetElapsedTime(lastReport, now).TotalMilliseconds < 250) return;
        lastReport = now;
        progress.Report(new(path, files, bytes));
    }
}
