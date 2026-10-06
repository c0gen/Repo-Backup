using RepoBackup.Core.Backup;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using RepoBackup.Core.Storage;

namespace RepoBackup.Core.Recovery;

public sealed class RestoreService(AppPaths paths, CatalogStore catalog, ResticClient restic, GitRecovery git)
{
    public async Task<string> RestoreAsync(Destination destination, SnapshotInfo snapshot, string target, string? selectedSnapshotPath = null, IProgress<BackupProgress>? progress = null, CancellationToken token = default)
    {
        using var lease = DestinationLease.Acquire(paths, destination.Path);
        await restic.PrepareRepositoryAsync(destination, token);
        var manifest = await restic.ManifestAsync(destination, snapshot, token);
        target = PathSafety.Normalize(target);
        PathSafety.EnsureDestinationOutsideSources(target, manifest.Sources.Select(s => s.OriginalPath).Append(destination.Path));
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Restore requires a new directory that does not already exist.");
        foreach (var source in manifest.Sources)
            if (source.RestoreFolder != PathSafety.SafeName(source.RestoreFolder) || source.SnapshotPath != PathSafety.SnapshotPath(source.OriginalPath)) throw new InvalidDataException("Unsafe source mapping in snapshot.");
        if (selectedSnapshotPath is not null && !manifest.Sources.Any(s => selectedSnapshotPath.Equals(s.SnapshotPath, StringComparison.OrdinalIgnoreCase) || selectedSnapshotPath.StartsWith(s.SnapshotPath.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Choose a file or folder inside a recorded project source.");
        Directory.CreateDirectory(target);
        try
        {
            var nodes = await restic.FilesAsync(destination, snapshot.Id, token);
            bool IsFile(SourceMapping source) => nodes.Any(n => n.Path.Equals(source.SnapshotPath, StringComparison.OrdinalIgnoreCase) && n.Type != "dir");
            if (selectedSnapshotPath is not null)
            {
                var source = manifest.Sources.OrderByDescending(s => s.SnapshotPath.Length).First(s => selectedSnapshotPath.Equals(s.SnapshotPath, StringComparison.OrdinalIgnoreCase) || selectedSnapshotPath.StartsWith(s.SnapshotPath.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
                var tree = IsFile(source) ? source.SnapshotPath[..source.SnapshotPath.LastIndexOf('/')] : source.SnapshotPath;
                var args = new List<string> { "restore", snapshot.Id + ":" + tree, "--target", Path.Combine(target, "sources", source.RestoreFolder), "--verify", "--overwrite", "never" };
                var relative = selectedSnapshotPath[tree.Length..]; if (!string.IsNullOrEmpty(relative)) { args.Add("--include"); args.Add(relative); }
                (await restic.RunAsync(destination, args, token)).EnsureSuccess("Restore selected path");
            }
            else
            {
                foreach (var source in manifest.Sources)
                {
                    progress?.Report(new(manifest.Project.Name, "Restoring " + source.RestoreFolder));
                    // A source may be a selected individual file rather than a directory.
                    var isFile = IsFile(source);
                    var args = isFile
                        ? new[] { "restore", snapshot.Id + ":" + source.SnapshotPath[..source.SnapshotPath.LastIndexOf('/')], "--target", Path.Combine(target, "sources", source.RestoreFolder), "--include", "/" + source.SnapshotPath[(source.SnapshotPath.LastIndexOf('/') + 1)..], "--verify", "--overwrite", "never" }
                        : new[] { "restore", snapshot.Id + ":" + source.SnapshotPath, "--target", Path.Combine(target, "sources", source.RestoreFolder), "--verify", "--overwrite", "never" };
                    (await restic.RunAsync(destination, args, token)).EnsureSuccess("Restore source");
                }
                if (manifest.Coverage == Coverage.FullProject) await git.RepairAsync(target, manifest.Sources, token);
            }
            await File.WriteAllTextAsync(Path.Combine(target, "recovery-manifest.json"), Json.Write(manifest), token);
            await File.WriteAllTextAsync(Path.Combine(target, "RESTORED.txt"), $"Restored snapshot {snapshot.Id} ({(snapshot.Successful ? "successful" : "incomplete or interrupted")}) on {DateTimeOffset.Now:O}.\nSources are in the sources folder. Original repositories were not modified.\n", token);
            catalog.SaveSetting("lastRestore", new { destination.Id, snapshotId = snapshot.Id, target, at = DateTimeOffset.UtcNow, successful = true });
            return target;
        }
        catch
        {
            await File.WriteAllTextAsync(Path.Combine(target, "RESTORE-INCOMPLETE.txt"), "Recovery did not finish. Files here may be incomplete. Retry into another new directory.", CancellationToken.None);
            throw;
        }
    }

    public async Task<int> RebuildCatalogAsync(Destination destination, CancellationToken token = default)
    {
        using var lease = DestinationLease.Acquire(paths, destination.Path);
        await restic.PrepareRepositoryAsync(destination, token); var count = 0;
        foreach (var snapshot in (await restic.SnapshotsAsync(destination, token)).OrderBy(s => s.Time))
        {
            var manifest = await restic.ManifestAsync(destination, snapshot, token);
            if (!catalog.Projects().Any(p => p.Id == manifest.Project.Id))
            { catalog.SaveProject(manifest.Project with { Reviewed = true, Enabled = false }); count++; }
            if (manifest.Selection is not null) catalog.SaveSelection(manifest.Selection);
            catalog.SaveJob(new JobRecord { Id = manifest.JobId, ProjectId = manifest.Project.Id, ProjectName = manifest.Selection?.Name ?? manifest.Project.Name, SeriesId = manifest.SeriesId, DestinationId = destination.Id, Coverage = manifest.Coverage, StartedAt = snapshot.Time, FinishedAt = snapshot.Time, Backup = snapshot.Successful ? Outcome.Successful : Outcome.Incomplete, Verification = snapshot.Successful ? Outcome.Successful : Outcome.NotRun, SnapshotId = snapshot.Id, Messages = ["History reconstructed from repository metadata."] });
        }
        return count;
    }
}
