using System.Diagnostics;
using System.Globalization;
using System.Text;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Backup;

public sealed partial class BackupService
{
    private async Task<JobRecord> CreateSnapshotAsync(Destination destination, ProjectEntry project, SavedSelection? selection,
        JobRecord job, List<SnapshotSummary> snapshots, BackupReadMode mode, bool useVss, IProgress<BackupProgress>? progress, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        progress?.Report(new(job.ProjectName, "Preparing sources", IsIndeterminate: true));
        var preview = await planner.PreviewAsync(project, selection, token, ScanProgress("Preparing sources"));
        PathSafety.EnsureDestinationOutsideSources(destination.Path, preview.Sources.Select(s => s.OriginalPath));
        job = job with { Issues = preview.Issues, Messages = preview.Warnings.Concat(preview.Issues.Select(i => i.Message)).Distinct().ToList() };
        catalog.SaveJob(job);
        if (preview.Files.Count == 0 && preview.Directories.Count == 0) throw new IOException("No readable files are available. " + string.Join("; ", job.Messages.Take(5)));
        if ((availableSpace ?? PathSafety.FreeSpace)(destination.Path) is long free && free < Math.Min(preview.Bytes + 16 * 1024 * 1024, 128 * 1024 * 1024)) throw new IOException("Insufficient free space for repository operations. Earlier backups have been preserved.");
        var decision = BackupReadPolicy.Select(snapshots, job.SeriesId, job.Coverage, Environment.MachineName, mode, DateTimeOffset.UtcNow, catalog.SeriesAliases(job.SeriesId));
        if (!useVss && decision.Mode == BackupReadMode.Incremental)
            preview.Issues.AddRange(SourceScanner.ReadabilityIssues(preview.Files, token, ScanProgress("Checking source readability")));
        job.PhaseMilliseconds["inventory"] = watch.Elapsed.TotalMilliseconds;
        job = job with { ReadMode = decision.Mode, ParentSnapshotId = decision.ParentId, LastFullReadAt = decision.FullReadAt,
            Files = preview.FileCount, Bytes = preview.Bytes, Messages = preview.Warnings.Concat(preview.Issues.Select(i => i.Message)).Distinct().ToList() };
        catalog.SaveJob(job);
        var working = Path.Combine(paths.JobsDirectory, job.Id); Directory.CreateDirectory(working);
        var manifestPath = Path.Combine(working, "manifest.json");
        var manifest = new RecoveryManifest { SchemaVersion = 4, GitContexts = preview.GitContexts, GitRegistrations = preview.GitRegistrations, Issues = preview.Issues,
            Directories = preview.Directories, GitMetadataDirectories = preview.GitMetadataDirectories, WslLinks = preview.WslLinks,
            JobId = job.Id, SeriesId = job.SeriesId, Project = project, Selection = selection, Coverage = job.Coverage, Sources = preview.Sources, Warnings = preview.Warnings };
        Recovery.WslLinkMetadata.Validate(manifest);
        var manifestJson = Json.Write(manifest);
        var stagingBytes = preview.Files.Where(f => f.WslFingerprint is null && (!f.IsDirectory || f.IsLink)).Sum(f => (long)Encoding.UTF8.GetByteCount(f.Path) + 1)
            + Encoding.UTF8.GetByteCount(manifestJson) + Encoding.UTF8.GetByteCount(manifestPath) + 1 + 16L * 1024 * 1024;
        if (PathSafety.FreeSpace(paths.DataDirectory) is { } stagingFree && stagingFree < stagingBytes)
            throw new IOException("Insufficient free space on the app data drive for the backup inventory and recovery manifest. Required: " + stagingBytes.ToString("N0") + " bytes. App data: " + paths.DataDirectory);
        await File.WriteAllTextAsync(manifestPath, manifestJson, new UTF8Encoding(false), token);
        var fileList = Path.Combine(working, "files.raw");
        await BackupStaging.WriteFileListAsync(fileList, preview.Files.Where(f => f.WslFingerprint is null && (!f.IsDirectory || f.IsLink)).Select(f => f.Path).Append(manifestPath), token);
        var args = new List<string> { "backup", "--files-from-raw", fileList, "--no-scan", "--host", Environment.MachineName,
            "--tag", "repobackup", "--tag", "pending", "--tag", "series=" + job.SeriesId, "--tag", "run=" + job.Id,
            "--tag", "coverage=" + (selection is null ? "full" : "partial"), "--tag", "read-mode=" + decision.Mode.ToString().ToLowerInvariant(),
            "--tag", "full-read-at=" + decision.FullReadAt.UtcTicks.ToString(CultureInfo.InvariantCulture),
            "--tag", "name=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(job.ProjectName)),
            "--tag", "manifest=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(PathSafety.SnapshotPath(manifestPath))) };
        if (decision.Mode == BackupReadMode.Full) args.Add("--force");
        else { args.Add("--parent"); args.Add(decision.ParentId!); args.Add("--group-by"); args.Add("host"); }
        if (preview.Issues.Any(i => i.Severity == BackupIssueSeverity.Advisory)) args.AddRange(["--tag", "advisories"]);
        if (useVss) args.Add("--use-fs-snapshot");
        var total = preview.Bytes + new FileInfo(manifestPath).Length;
        var output = new BackupOutputProgress(job.ProjectName, useVss ? "Backing up · VSS" : "Backing up · live",
            total, preview.FileCount + 1, working, progress);
        progress?.Report(new(job.ProjectName, useVss ? "Backing up · VSS" : "Backing up · live", TotalBytes: total, TotalFiles: preview.FileCount + 1));
        watch.Restart();
        var result = await restic.RunAsync(destination, args, token, output.OnOutput, captureOutput: false);
        var snapshotId = output.SnapshotId;
        job.PhaseMilliseconds["backup"] = watch.Elapsed.TotalMilliseconds;
        job = job with { SnapshotId = snapshotId }; catalog.SaveJob(job);
        if (result.ExitCode is not (0 or 3)) result.EnsureSuccess("Create backup");
        if (snapshotId is null) throw new IOException("Restic did not report a completed snapshot.");
        if (!string.IsNullOrWhiteSpace(result.Error)) job = job with { Messages = [.. job.Messages, result.Error.Trim()] };
        watch.Restart();
        progress?.Report(new(job.ProjectName, useVss ? "Checking WSL link coverage" : "Checking live coverage", IsIndeterminate: true));
        var coverageStage = useVss ? "Checking WSL link coverage" : "Checking live coverage";
        var after = await planner.PreviewAsync(project, selection, token, ScanProgress(coverageStage));
        if (!useVss && decision.Mode == BackupReadMode.Incremental)
            after.Issues.AddRange(SourceScanner.ReadabilityIssues(after.Files, token, ScanProgress("Checking source readability")));
        var changes = SourceChanges.Compare(preview, after, metadataOnly: useVss);
        var changed = changes.HasChanges;
        job.PhaseMilliseconds["coverage"] = watch.Elapsed.TotalMilliseconds;
        var issues = preview.Issues.Concat(after.Issues).Distinct().ToList();
        issues.AddRange(preview.Warnings.Concat(after.Warnings).Distinct().Where(w => !issues.Any(i => i.Message == w))
            .Select(w => new BackupIssue("source.warning", BackupIssueSeverity.CoverageError, null, w)));
        job = job with { Issues = issues, SourceChanges = changes, Messages = job.Messages.Concat(after.Warnings).Concat(issues.Select(i => i.Message)).Distinct().ToList() };
        if (changed) job = job with { Messages = [.. job.Messages, changes.ToString(), "Files or source metadata changed during the backup. This snapshot has incomplete coverage; no retention cleanup ran for this series."] };
        var complete = result.ExitCode == 0 && preview.Complete && after.Complete && !changed && string.IsNullOrWhiteSpace(result.Error);
        return job with { Backup = complete ? Outcome.Running : Outcome.Incomplete, Verification = Outcome.Running };

        IProgress<SourceScanProgress> ScanProgress(string stage) => new ScanCallback(p =>
            progress?.Report(new(job.ProjectName, stage, Files: p.Files, Bytes: p.Bytes, CurrentFile: p.CurrentPath, IsIndeterminate: true)));
    }

    private sealed class ScanCallback(Action<SourceScanProgress> report) : IProgress<SourceScanProgress>
    { public void Report(SourceScanProgress value) => report(value); }
}
