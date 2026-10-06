using System.Security.Principal;
using System.Text;
using System.Text.Json;
using RepoBackup.Core.Discovery;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using RepoBackup.Core.Storage;
using RepoBackup.Core.Windows;

namespace RepoBackup.Core.Backup;

public sealed partial class BackupService(AppPaths paths, CatalogStore catalog, CredentialStore credentials, ResticClient restic, SelectionPlanner planner, ProjectDiscoveryService discovery, Func<string, long?>? availableSpace = null)
{
    public async Task<VerificationRecord> VerifyAsync(Destination destination, CancellationToken token = default)
    {
        var record = new VerificationRecord(destination.Id, DateTimeOffset.UtcNow, Outcome.Running, "Verification started");
        try
        {
            using var lease = DestinationLease.Acquire(paths, destination.Path);
            await restic.PrepareRepositoryAsync(destination, token);
            var result = await restic.RunAsync(destination, ["check", "--read-data"], token); result.EnsureSuccess("Verify repository data");
            record = record with { Outcome = Outcome.Successful, Message = "All repository data passed verification." };
            catalog.SaveDestination(destination with { VerifiedAt = record.At });
        }
        catch (OperationCanceledException) { record = record with { Outcome = Outcome.Cancelled, Message = "Verification cancelled." }; }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { record = record with { Outcome = Outcome.Failed, Message = e.Message }; }
        var history = catalog.GetSetting<List<VerificationRecord>>("verificationHistory") ?? [];
        catalog.SaveSetting("verificationHistory", history.Append(record).TakeLast(100).ToList()); return record;
    }

    public async Task<List<JobRecord>> RunAsync(Destination destination, IEnumerable<ProjectEntry> requestedProjects, SavedSelection? selection = null,
        IProgress<BackupProgress>? progress = null, CancellationToken token = default, bool useVss = false)
    {
        if (useVss && !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) throw new InvalidOperationException("VSS was selected. Run Repo Backup as Administrator to use volume snapshots; the run has not fallen back to live mode.");
        var projects = requestedProjects.ToList();
        if (projects.Count == 0) throw new InvalidOperationException("Select at least one project.");
        if (projects.Any(p => !p.Reviewed || p.Dismissed)) throw new InvalidOperationException("Review discovered projects before including them in a backup.");
        DestinationLease? acquired = null;
        try
        {
            await discovery.RefreshAsync(token: token);
            acquired = DestinationLease.Acquire(paths, destination.Path);
            await restic.PrepareRepositoryAsync(destination, token);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            acquired?.Dispose();
            var failed = projects.Select(p => new JobRecord { ProjectId = p.Id, ProjectName = selection?.Name ?? p.Name, SeriesId = selection is null ? "project-" + p.Id : "selection-" + selection.Id, DestinationId = destination.Id, Coverage = selection is null ? Coverage.FullProject : Coverage.PartialSelection, Backup = e is OperationCanceledException ? Outcome.Cancelled : Outcome.Failed, FinishedAt = DateTimeOffset.UtcNow, Messages = ["Destination preparation did not finish: " + e.Message] }).ToList();
            foreach (var job in failed) catalog.SaveJob(job);
            return failed;
        }
        catch
        {
            acquired?.Dispose();
            throw;
        }
        using var lease = acquired;
        var results = new List<JobRecord>();
        foreach (var requested in projects)
        {
            if (token.IsCancellationRequested)
            {
                var cancelled = new JobRecord { ProjectId = requested.Id, ProjectName = selection?.Name ?? requested.Name, SeriesId = selection is null ? "project-" + requested.Id : "selection-" + selection.Id, DestinationId = destination.Id, Coverage = selection is null ? Coverage.FullProject : Coverage.PartialSelection, Backup = Outcome.Cancelled, FinishedAt = DateTimeOffset.UtcNow, Messages = ["Cancelled before this project started."] };
                catalog.SaveJob(cancelled); results.Add(cancelled); continue;
            }
            var project = catalog.Projects().Single(p => p.Id == requested.Id);
            var job = new JobRecord { ProjectId = project.Id, ProjectName = selection?.Name ?? project.Name, SeriesId = selection is null ? "project-" + project.Id : "selection-" + selection.Id, DestinationId = destination.Id, Coverage = selection is null ? Coverage.FullProject : Coverage.PartialSelection };
            catalog.SaveJob(job);
            try
            {
                progress?.Report(new(job.ProjectName, "Preparing sources"));
                var preview = await planner.PreviewAsync(project, selection, token);
                PathSafety.EnsureDestinationOutsideSources(destination.Path, preview.Sources.Select(s => s.OriginalPath));
                if (preview.Files.Count == 0) throw new IOException("No readable files are available. " + string.Join("; ", preview.Warnings.Take(5)));
                if ((availableSpace ?? PathSafety.FreeSpace)(destination.Path) is long free && free < Math.Min(preview.Bytes + 16 * 1024 * 1024, 128 * 1024 * 1024)) throw new IOException("Insufficient free space for repository operations. Earlier backups have been preserved.");
                var working = Path.Combine(paths.JobsDirectory, job.Id); Directory.CreateDirectory(working);
                var manifestPath = Path.Combine(working, "manifest.json");
                var manifest = new RecoveryManifest { JobId = job.Id, SeriesId = job.SeriesId, Project = project, Selection = selection, Coverage = job.Coverage, Sources = preview.Sources, Warnings = preview.Warnings };
                await File.WriteAllTextAsync(manifestPath, Json.Write(manifest), new UTF8Encoding(false), token);
                var fileList = Path.Combine(working, "files.raw");
                await File.WriteAllTextAsync(fileList, string.Join('\0', preview.Files.Select(f => f.Path).Append(manifestPath)) + '\0', new UTF8Encoding(false), token);
                var args = new List<string> { "backup", "--files-from-raw", fileList, "--force", "--tag", "repobackup", "--tag", "pending", "--tag", "series=" + job.SeriesId, "--tag", "run=" + job.Id, "--tag", "coverage=" + (selection is null ? "full" : "partial"), "--tag", "name=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(job.ProjectName)), "--tag", "manifest=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(PathSafety.SnapshotPath(manifestPath))) };
                if (useVss) args.Add("--use-fs-snapshot");
                string? snapshotId = null;
                var result = await restic.RunAsync(destination, args, token, line =>
                {
                    using var message = JsonDocument.Parse(line);
                    var value = message.RootElement;
                    if (!value.TryGetProperty("message_type", out var type)) return;
                    if (type.GetString() == "summary" && value.TryGetProperty("snapshot_id", out var snapshot)) snapshotId = snapshot.GetString();
                    if (type.GetString() == "status") progress?.Report(new(job.ProjectName, useVss ? "Backing up · VSS" : "Backing up · live", value.TryGetProperty("percent_done", out var percent) ? percent.GetDouble() : 0, value.TryGetProperty("files_done", out var count) ? count.GetInt64() : 0, value.TryGetProperty("bytes_done", out var bytes) ? bytes.GetInt64() : 0));
                });
                job = job with { SnapshotId = snapshotId, Files = preview.FileCount, Bytes = preview.Bytes, Messages = [.. preview.Warnings] };
                catalog.SaveJob(job);
                if (result.ExitCode is not (0 or 3)) result.EnsureSuccess("Create backup");
                if (snapshotId is null) throw new IOException("Restic did not report a completed snapshot.");
                if (!string.IsNullOrWhiteSpace(result.Error)) job = job with { Messages = [.. job.Messages, result.Error.Trim()] };
                var changed = false;
                if (!useVss)
                {
                    progress?.Report(new(job.ProjectName, "Checking live coverage", 1));
                    var after = await planner.PreviewAsync(project, selection, token);
                    changed = SelectionPlanner.Changed(preview, after) || !after.Complete;
                    if (changed) job = job with { Messages = [.. job.Messages, "Files or source availability changed during the live backup. This snapshot has incomplete coverage; no retention cleanup ran.", .. after.Warnings] };
                }
                var complete = result.ExitCode == 0 && preview.Complete && !changed && string.IsNullOrWhiteSpace(result.Error);
                job = job with { Backup = complete ? Outcome.Running : Outcome.Incomplete, Verification = Outcome.Running };
                catalog.SaveJob(job); progress?.Report(new(job.ProjectName, "Verifying repository", 1));
                (await restic.RunAsync(destination, ["check"], token)).EnsureSuccess("Verify new backup");
                job = job with { Verification = Outcome.Successful };
                catalog.SaveJob(job);
                if (complete)
                {
                    progress?.Report(new(job.ProjectName, "Finalizing verified snapshot", 1));
                    (await restic.RunAsync(destination, ["tag", "--remove", "pending", "--add", "successful", snapshotId], token)).EnsureSuccess("Mark verified backup successful");
                    var snapshots = await restic.SnapshotsAsync(destination, token);
                    job = job with { Backup = Outcome.Successful, SnapshotId = snapshots.Single(s => s.TagValue("run") == job.Id && s.Successful).Id, Cleanup = Outcome.Running };
                    catalog.SaveJob(job); progress?.Report(new(job.ProjectName, "Keeping latest ten versions", 1));
                    token.ThrowIfCancellationRequested();
                    // Use explicit successful IDs in this stable series. Host, changing paths and per-run tags cannot split retention groups.
                    var obsolete = snapshots.Where(s => s.Successful && s.TagValue("series") == job.SeriesId && s.Coverage == job.Coverage).OrderByDescending(s => s.Time).Skip(10).Select(s => s.Id).ToList();
                    if (obsolete.Count > 0)
                    {
                        (await restic.RunAsync(destination, new[] { "forget" }.Concat(obsolete), token)).EnsureSuccess("Remove obsolete snapshots");
                        (await restic.RunAsync(destination, ["prune"], token)).EnsureSuccess("Reclaim unused data");
                    }
                    job = job with { Cleanup = Outcome.Successful };
                }
                job = job with { FinishedAt = DateTimeOffset.UtcNow };
            }
            catch (OperationCanceledException)
            {
                job = job with { Backup = job.Backup == Outcome.Running ? Outcome.Cancelled : job.Backup, Verification = job.Verification == Outcome.Running ? Outcome.Cancelled : job.Verification, Cleanup = job.Cleanup == Outcome.Running ? Outcome.Cancelled : job.Cleanup, FinishedAt = DateTimeOffset.UtcNow, Messages = [.. job.Messages, "Cancelled. Existing recovery points are preserved."] };
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException)
            {
                job = job with { Backup = job.Backup == Outcome.Running ? Outcome.Failed : job.Backup, Verification = job.Verification == Outcome.Running ? Outcome.Failed : job.Verification, Cleanup = job.Cleanup == Outcome.Running ? Outcome.Failed : job.Cleanup, FinishedAt = DateTimeOffset.UtcNow, Messages = [.. job.Messages, e.Message] };
            }
            catalog.SaveJob(job); results.Add(job); progress?.Report(new(job.ProjectName, job.Backup.ToString(), 1, job.Files, job.Bytes));
        }
        return results;
    }
}
