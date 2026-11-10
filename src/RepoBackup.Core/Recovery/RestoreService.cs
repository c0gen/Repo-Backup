using RepoBackup.Core.Backup;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using RepoBackup.Core.Storage;
using RepoBackup.Core.Windows;

namespace RepoBackup.Core.Recovery;

public sealed class RestoreService(AppPaths paths, CatalogStore catalog, ResticClient restic, GitRecovery git,
    Action<string, WslLinkData, string>? createWslLink = null)
{
    public async Task<string> RestoreAsync(Destination destination, SnapshotSummary snapshot, string target, string? selectedSnapshotPath = null, IProgress<BackupProgress>? progress = null, CancellationToken token = default)
    {
        progress?.Report(new("", "Preparing restore", IsIndeterminate: true));
        using var lease = DestinationLease.Acquire(paths, destination.Path);
        await restic.PrepareRepositoryAsync(destination, token);
        var manifest = await restic.ManifestAsync(destination, snapshot, token);
        target = PathSafety.Normalize(target);
        PathSafety.EnsureDestinationOutsideSources(target, manifest.Sources.Select(s => s.OriginalPath).Append(destination.Path));
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Restore requires a new directory that does not already exist.");
        foreach (var source in manifest.Sources)
            if (source.RestoreFolder != PathSafety.SafeName(source.RestoreFolder) || !ManifestPaths.ValidSnapshotPath(source, manifest.SchemaVersion)) throw new InvalidDataException("Unsafe source mapping in snapshot.");
        progress?.Report(new(manifest.Project.Name, "Reading snapshot contents", IsIndeterminate: true));
        var nativeNodes = await restic.FilesAsync(destination, snapshot.Id, token);
        var originalManifest = manifest;
        manifest = LegacySnapshotMappings.Adapt(manifest, nativeNodes);
        selectedSnapshotPath = LegacySnapshotMappings.AdaptSelection(originalManifest, manifest, selectedSnapshotPath);
        if (selectedSnapshotPath is not null && !manifest.Sources.Any(s => selectedSnapshotPath.Equals(s.SnapshotPath, StringComparison.OrdinalIgnoreCase) || selectedSnapshotPath.StartsWith(s.SnapshotPath.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Choose a file or folder inside a recorded project source.");
        var nodes = SnapshotContents.Merge(manifest, nativeNodes);
        var nativePaths = nativeNodes.Select(n => n.Path).ToHashSet(StringComparer.Ordinal);
        var sources = selectedSnapshotPath is null ? manifest.Sources : manifest.Sources.OrderByDescending(s => s.SnapshotPath.Length)
            .Where(s => selectedSnapshotPath.Equals(s.SnapshotPath, StringComparison.Ordinal) || selectedSnapshotPath.StartsWith(s.SnapshotPath.TrimEnd('/') + "/", StringComparison.Ordinal)).Take(1).ToList();
        if (sources.Count == 0) throw new ArgumentException("Choose a path inside a recorded source.");
        var plans = sources.Select(s => RestorePaths.Plan(s, nodes, selectedSnapshotPath)).ToList();
        var reporting = new RestoreProgress(manifest.Project.Name, plans, progress);
        var verificationRepairs = new List<RestoreFileRepair>();
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(target);
        try
        {
            RestoreLinkRequirements.EnsureSupported(target, plans.SelectMany(p => p.Expected).Where(n => nativePaths.Contains(n.Path)));
            foreach (var plan in plans)
            {
                token.ThrowIfCancellationRequested();
                reporting.Begin(plan);
                var nativePlan = plan with { Expected = plan.Expected.Where(n => nativePaths.Contains(n.Path)).ToList() };
                if (nativePlan.Expected.Count > 0)
                {
                    var args = new List<string> { "restore", snapshot.Id + ":" + plan.Tree, "--target", Path.Combine(target, "sources", plan.Source.RestoreFolder), "--verify", "--overwrite", "never", "--verbose=2" };
                    if (plan.Include is not null) { args.Add("--include"); args.Add(plan.Include); }
                    var result = await restic.RunAsync(destination, args, token, reporting.OnOutput, captureOutput: false);
                    verificationRepairs.AddRange(await RestoreVerificationRepair.RepairAsync(restic, destination, snapshot,
                        nativePlan, target, result, stage => reporting.Phase(stage), token));
                }
                reporting.Phase("Checking restored contents · " + plan.Source.RestoreFolder);
                nativePlan.Verify(target, token);
                RestorePaths.CreateManifestDirectories(plan, nativePaths, target);
                reporting.CompleteSource();
            }
            return await new RestoreProjectFinalizer(catalog, git, createWslLink).FinishAsync(destination, snapshot, manifest,
                target, plans, nativePaths, reporting, verificationRepairs, selectedSnapshotPath is null && manifest.Coverage == Coverage.FullProject, token);
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
        foreach (var snapshot in (await restic.SnapshotSummariesAsync(destination, token)).OrderByDescending(s => s.Time))
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
