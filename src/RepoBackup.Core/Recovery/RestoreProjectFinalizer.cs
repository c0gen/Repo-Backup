using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using RepoBackup.Core.Storage;
using RepoBackup.Core.Windows;

namespace RepoBackup.Core.Recovery;

// Shared finalization after all requested native contents have passed verification.
public sealed class RestoreProjectFinalizer(CatalogStore catalog, GitRecovery git,
    Action<string, WslLinkData, string>? createWslLink = null)
{
    public async Task<string> FinishAsync(Destination destination, SnapshotSummary snapshot, RecoveryManifest manifest,
        string target, IReadOnlyList<RestorePathPlan> plans, IReadOnlySet<string> nativePaths, RestoreProgress reporting,
        IReadOnlyList<RestoreFileRepair> verificationRepairs, bool fullProjectRestore, CancellationToken token = default)
    {
        PathSafety.EnsureDestinationOutsideSources(target, manifest.Sources.Select(s => s.OriginalPath).Append(destination.Path));
        foreach (var plan in plans)
            (plan with { Expected = plan.Expected.Where(n => nativePaths.Contains(n.Path)).ToList() }).Verify(target, token);
        if (fullProjectRestore)
        {
            reporting.Phase("Repairing restored Git relationships");
            await git.RepairAsync(target, manifest.Sources, token, manifest.GitMetadataDirectories, manifest.GitContexts, manifest.GitRegistrations);
        }
        return await CompleteAfterGitAsync(destination, snapshot, manifest, target, plans, nativePaths, reporting, verificationRepairs, token);
    }

    // For callers that have already verified native contents and completed Git repair.
    public async Task<string> CompleteAfterGitAsync(Destination destination, SnapshotSummary snapshot, RecoveryManifest manifest,
        string target, IReadOnlyList<RestorePathPlan> plans, IReadOnlySet<string> nativePaths, RestoreProgress reporting,
        IReadOnlyList<RestoreFileRepair> verificationRepairs, CancellationToken token = default)
    {
        PathSafety.EnsureDestinationOutsideSources(target, manifest.Sources.Select(s => s.OriginalPath).Append(destination.Path));
        var archived = await GitArchivePaths.ReadAsync(target, manifest, token);
        reporting.Phase("Restoring and verifying WSL links");
        foreach (var plan in plans)
        {
            var linkPaths = new HashSet<string>(StringComparer.Ordinal);
            var links = manifest.WslLinks.Where(l => l.SourceId == plan.Source.Id)
                .ToDictionary(l => WslLinkMetadata.SnapshotPath(plan.Source, l), StringComparer.Ordinal);
            foreach (var node in plan.Expected)
            {
                token.ThrowIfCancellationRequested();
                if (!links.TryGetValue(node.Path, out var link)) continue;
                (createWslLink ?? WslLinks.Create)(archived.TargetPath(plan, node, target), link.Data, target);
                linkPaths.Add(node.Path);
            }
            // Git can legitimately move captured empty directories into the orphan archive.
            foreach (var node in plan.Expected.Where(n => !nativePaths.Contains(n.Path) && !linkPaths.Contains(n.Path)))
            {
                token.ThrowIfCancellationRequested();
                RestorePaths.VerifyNode(archived.TargetPath(plan, node, target), node);
            }
        }
        reporting.Phase("Finalizing restore");
        if (verificationRepairs.Count > 0)
            await File.WriteAllTextAsync(Path.Combine(target, "restore-verification-repairs.json"), Json.Write(verificationRepairs.Select(r =>
                new BackupIssue("RecoveryFileRepaired", BackupIssueSeverity.Advisory, r.Path, r.Reason)).ToList()), token);
        await File.WriteAllTextAsync(Path.Combine(target, "recovery-manifest.json"), Json.Write(manifest), token);
        await File.WriteAllTextAsync(Path.Combine(target, "RESTORED.txt"), $"Restored snapshot {snapshot.Id} ({(snapshot.Successful ? "successful" : "incomplete or interrupted")}) on {DateTimeOffset.Now:O}.\nSources are in the sources folder. Original repositories were not modified.\n" +
            (verificationRepairs.Count == 0 ? "" : $"Verified retry repaired {verificationRepairs.Count} recovery files. See restore-verification-repairs.json.\n"), token);
        token.ThrowIfCancellationRequested();
        File.Delete(Path.Combine(target, "RESTORE-INCOMPLETE.txt"));
        catalog.SaveSetting("lastRestore", new { destination.Id, snapshotId = snapshot.Id, target, at = DateTimeOffset.UtcNow, successful = true });
        reporting.Complete();
        return target;
    }
}
