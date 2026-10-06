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
        var nodes = await restic.FilesAsync(destination, snapshot.Id, token);
        var sources = selectedSnapshotPath is null ? manifest.Sources : manifest.Sources.OrderByDescending(s => s.SnapshotPath.Length)
            .Where(s => selectedSnapshotPath.Equals(s.SnapshotPath, StringComparison.Ordinal) || selectedSnapshotPath.StartsWith(s.SnapshotPath.TrimEnd('/') + "/", StringComparison.Ordinal)).Take(1).ToList();
        if (sources.Count == 0) throw new ArgumentException("Choose a path inside a recorded source.");
        var plans = sources.Select(s => RestorePaths.Plan(s, nodes, selectedSnapshotPath)).ToList();
        Directory.CreateDirectory(target);
        try
        {
            foreach (var plan in plans)
            {
                progress?.Report(new(manifest.Project.Name, "Restoring " + plan.Source.RestoreFolder));
                var args = new List<string> { "restore", snapshot.Id + ":" + plan.Tree, "--target", Path.Combine(target, "sources", plan.Source.RestoreFolder), "--verify", "--overwrite", "never" };
                if (plan.Include is not null) { args.Add("--include"); args.Add(plan.Include); }
                (await restic.RunAsync(destination, args, token)).EnsureSuccess("Restore requested contents");
                plan.Verify(target);
            }
            if (selectedSnapshotPath is null && manifest.Coverage == Coverage.FullProject) await git.RepairAsync(target, manifest.Sources, token);
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
        await restic.PrepareRepositoryAsync(destination, token);
        var manifests = new List<RecoveryManifest>(); var history = new List<JobRecord>();
        foreach (var snapshot in (await restic.SnapshotsAsync(destination, token)).OrderByDescending(s => s.Time))
        {
            RecoveryManifest manifest;
            try
            {
                manifest = await restic.ManifestAsync(destination, snapshot, token);
                RecoveryMetadata.Validate(manifest, snapshot);
            }
            catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException or FormatException or ArgumentException) { continue; }
            manifests.Add(manifest);
            history.Add(new JobRecord { Id = manifest.JobId, ProjectId = manifest.Project.Id, ProjectName = manifest.Selection?.Name ?? manifest.Project.Name, SeriesId = manifest.SeriesId, DestinationId = destination.Id, Coverage = manifest.Coverage, StartedAt = snapshot.Time, FinishedAt = snapshot.Time, Backup = snapshot.Successful ? Outcome.Successful : Outcome.Incomplete, Verification = snapshot.Successful ? Outcome.Successful : Outcome.NotRun, SnapshotId = snapshot.Id, Messages = ["History reconstructed from repository metadata."] });
        }
        token.ThrowIfCancellationRequested();
        return catalog.RecoverCatalog(manifests, history);
    }
}
