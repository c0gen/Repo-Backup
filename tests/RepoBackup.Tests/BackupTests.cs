using RepoBackup.Core.Application;
using RepoBackup.Core.Backup;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class BackupTests
{
    public static async Task RunAsync(TestRunner tests, Fixture fixture, DestinationProtection protection = DestinationProtection.RecoveryKey)
    {
        Console.WriteLine("Backup/recovery suite: " + protection);
        var app = fixture.App;
        await tests.Run("Create dirty Git repository with two worktrees and a local submodule", fixture.PrepareAsync);
        await tests.Run("Preview includes Git, dirty files, ignored config, assets and worktrees", async () =>
        {
            var preview = await app.Planner.PreviewAsync(fixture.Project); Assert(preview.Complete, string.Join("\n", preview.Warnings));
            Assert(preview.Sources.Count == 3, "Expected main repo plus two worktrees.");
            Assert(preview.Files.Any(f => f.Path == Path.Combine(fixture.Main, ".env")) && preview.Files.Any(f => f.Path.EndsWith(".git\\index")), "Configuration or staged data missing.");
            Assert(preview.Files.Any(f => f.Path == Path.Combine(fixture.Worktree, "unfinished.txt")), "Worktree changes missing.");
        });
        await tests.Run("Default exclusions preserve tracked dependency files, build and dist", async () => { var preview = await app.Planner.PreviewAsync(fixture.Project); Assert(preview.Files.Any(f => f.Path.EndsWith("node_modules\\tracked.txt")), "Tracked dependency excluded."); Assert(!preview.Files.Any(f => f.Path.EndsWith("node_modules\\cache.txt")), "Dependency cache included."); Assert(preview.Files.Any(f => f.Path.EndsWith("build\\keep.txt")) && preview.Files.Any(f => f.Path.EndsWith("dist\\keep.txt")), "Generic build folders excluded."); });
        await tests.Run("Exclusion overrides change the actual planned contents", async () => { var rules = fixture.Project.Exclusions with { ExcludedDirectories = [] }; var preview = await app.Planner.PreviewAsync(fixture.Project with { Exclusions = rules }); Assert(preview.Files.Any(f => f.Path.EndsWith("node_modules\\cache.txt")), "Override ignored."); });
        await tests.Run("Initialize a pinned restic repository and record its identity", async () => { fixture.Destination = await app.Backups.AddDestinationAsync("Backup Drive", Path.Combine(fixture.Root, "repository"), protection: protection); Assert(fixture.Destination.RepositoryId?.Length == 64 && fixture.Destination.Protection == protection, "Repository identity or protection missing."); });
        await tests.Run("Imported destinations reconnect their key without losing identity or a valid existing key", async () =>
        {
            var destination = fixture.Destination;
            string? key = null;
            if (protection == DestinationProtection.RecoveryKey)
            {
                key = app.Credentials.Get(destination.Id);
                await Throws<IOException>(() => app.Backups.AddDestinationAsync("Wrong key", destination.Path, "a-deliberately-invalid-recovery-key", openExisting: true));
                Assert(app.Credentials.Get(destination.Id) == key, "Invalid recovery key destroyed the existing credential.");
                app.Credentials.Delete(destination.Id);
            }
            else
            {
                await Throws<InvalidOperationException>(() => app.Backups.AddDestinationAsync("Wrong mode", destination.Path, "a-deliberately-invalid-recovery-key", openExisting: true));
                Assert(!app.Credentials.Exists(destination.Id), "Password-free repository generated a credential.");
            }
            var reconnected = await app.Backups.AddDestinationAsync(destination.Name, destination.Path, key, openExisting: true, protection: protection);
            Assert(reconnected.Id == destination.Id && app.Catalog.Destinations().Count == 1, "Destination identity was duplicated."); fixture.Destination = reconnected;
        });
        await tests.Run("Backup creates one verified successful snapshot for the project", async () => { var result = (await app.Backups.RunAsync(fixture.Destination, [fixture.Project])).Single(); Assert(result.Backup == Outcome.Successful && result.Verification == Outcome.Successful && result.Cleanup == Outcome.Successful, Json.Write(result)); fixture.Snapshot = (await app.Restic.SnapshotsAsync(fixture.Destination)).Single(); Assert(fixture.Snapshot.Successful, "Snapshot not marked successful."); });
        await tests.Run("Snapshot contains recoverable source mappings independently of the catalog", async () => { var manifest = await app.Restic.ManifestAsync(fixture.Destination, fixture.Snapshot); Assert(manifest.Sources.Count == 3 && manifest.Project.Id == fixture.Project.Id, "Recovery metadata missing."); var files = await app.Restic.FilesAsync(fixture.Destination, fixture.Snapshot.Id); Assert(files.Any(f => f.Path == PathSafety.SnapshotPath(Path.Combine(fixture.Main, ".env"))), "Snapshot path mapping incorrect."); });
        await tests.Run("Full restore preserves commits, branches, dirty index and multiple worktrees", async () =>
        {
            var progress = new ProgressRecorder();
            var target = Path.Combine(fixture.Root, "restored-full"); await app.Restore.RestoreAsync(fixture.Destination, fixture.Snapshot, target, progress: progress);
            progress.AssertSuccessfulRestore();
            Assert(progress.Items.Count(p => p.Stage.StartsWith("Restoring ") && p.CurrentFile is null) >= 3
                && progress.Items.Any(p => p.CurrentFile is not null) && progress.Items.Any(p => p.Stage.StartsWith("Verifying ") && p.IsIndeterminate), "Real restore did not stream source and file activity.");
            var manifest = await app.Restic.ManifestAsync(fixture.Destination, fixture.Snapshot); var main = Path.Combine(target, "sources", manifest.Sources.Single(s => s.OriginalPath == fixture.Main).RestoreFolder);
            Assert(await fixture.GitOutput(main, "rev-parse", "HEAD") == await fixture.GitOutput(fixture.Main, "rev-parse", "HEAD"), "Commits changed.");
            Assert(await fixture.GitOutput(main, "branch", "--format=%(refname)") == await fixture.GitOutput(fixture.Main, "branch", "--format=%(refname)"), "Branches changed.");
            Assert(await fixture.GitOutput(main, "status", "--porcelain=v1", "-z") == await fixture.GitOutput(fixture.Main, "status", "--porcelain=v1", "-z"), "Dirty status changed.");
            var staged = await fixture.GitOutput(main, "show", ":staged.txt");
            Assert(staged == await fixture.GitOutput(fixture.Main, "show", ":staged.txt") && staged.TrimEnd('\r', '\n') == "staged version", "Staged index not preserved.");
            foreach (var source in manifest.Sources.Where(s => s.Kind == SourceKind.Worktree)) { var restored = Path.Combine(target, "sources", source.RestoreFolder); Assert((await fixture.GitOutput(restored, "rev-parse", "--git-common-dir")).Contains(target.Replace('\\', '/')), "Worktree still points at original database."); }
            Assert((await fixture.GitOutput(main, "worktree", "list", "--porcelain")).Contains(target.Replace('\\', '/')), "Restored worktrees not linked.");
        });
        await tests.Run("Full restore hashes match working files and preserves local submodule data", async () =>
        {
            var manifest = await app.Restic.ManifestAsync(fixture.Destination, fixture.Snapshot); var main = Path.Combine(fixture.Root, "restored-full", "sources", manifest.Sources.Single(s => s.OriginalPath == fixture.Main).RestoreFolder);
            foreach (var relative in new[] { "README.md", "staged.txt", "untracked.txt", ".env", "build\\keep.txt", "dist\\keep.txt", "node_modules\\tracked.txt", "module\\module.txt" }) Assert(await Fixture.HashAsync(Path.Combine(main, relative)) == await Fixture.HashAsync(Path.Combine(fixture.Main, relative)), "Hash mismatch: " + relative);
            Assert(!File.Exists(Path.Combine(main, "node_modules", "cache.txt")), "Excluded cache restored."); Assert((await fixture.GitOutput(Path.Combine(main, "module"), "rev-parse", "--show-toplevel")).Trim().Equals(Path.Combine(main, "module").Replace('\\', '/'), StringComparison.OrdinalIgnoreCase), "Submodule still points outside recovery.");
        });
        await RestoreServiceProgressTests.RunAsync(tests, fixture);
        await tests.Run("Recovery writes do not modify any file in the original repository", async () => { var after = await Fixture.HashesAsync(fixture.Main); Assert(fixture.OriginalHashes.Count == after.Count && fixture.OriginalHashes.All(kv => after.GetValueOrDefault(kv.Key) == kv.Value), "Original repository was modified."); });
        await tests.Run("Individual file restores match hashes in a new directory", async () =>
        {
            var progress = new ProgressRecorder(); var target = Path.Combine(fixture.Root, "restored-file"); var path = PathSafety.SnapshotPath(Path.Combine(fixture.Main, ".env"));
            await app.Restore.RestoreAsync(fixture.Destination, fixture.Snapshot, target, path, progress); progress.AssertSuccessfulRestore();
            Assert(progress.Items.Any(p => p.CurrentFile?.EndsWith("\\.env") == true), "Selected file did not report activity.");
            var restored = Directory.EnumerateFiles(target, ".env", SearchOption.AllDirectories).Single(); Assert(await Fixture.HashAsync(restored) == await Fixture.HashAsync(Path.Combine(fixture.Main, ".env")), "Selective hash mismatch."); Assert(!Directory.EnumerateFiles(target, "README.md", SearchOption.AllDirectories).Any(), "Selective restore included unrelated files.");
        });
        await tests.Run("Restoring into an original or existing directory is rejected", async () => { await Throws<InvalidOperationException>(() => app.Restore.RestoreAsync(fixture.Destination, fixture.Snapshot, Path.Combine(fixture.Main, "restored"))); await Throws<IOException>(() => app.Restore.RestoreAsync(fixture.Destination, fixture.Snapshot, Path.Combine(fixture.Root, "restored-file"))); });
        await tests.Run("Eleven successful full backups retain exactly ten full recovery points", async () =>
        {
            for (var i = 1; i < 11; i++) { await File.WriteAllTextAsync(Path.Combine(fixture.Main, "untracked.txt"), "Recovery version " + i); var result = (await app.Backups.RunAsync(fixture.Destination, [fixture.Project])).Single(); Assert(result.Backup == Outcome.Successful && result.Cleanup == Outcome.Successful, Json.Write(result)); }
            var snapshots = await app.Restic.SnapshotsAsync(fixture.Destination); Assert(snapshots.Count(s => s.Successful && s.TagValue("series") == "project-" + fixture.Project.Id) == 10, "Retention failed.");
        });
        var selection = new SavedSelection { ProjectId = fixture.Project.Id, Name = "Design Assets", Paths = [Path.Combine(fixture.Main, "assets")] };
        await tests.Run("Partial selections have separate snapshots and cannot displace full versions", async () => { app.Catalog.SaveSelection(selection); var result = (await app.Backups.RunAsync(fixture.Destination, [fixture.Project], selection)).Single(); Assert(result.Coverage == Coverage.PartialSelection && result.Backup == Outcome.Successful, Json.Write(result)); var snapshots = await app.Restic.SnapshotsAsync(fixture.Destination); Assert(snapshots.Count(s => s.Successful && s.Coverage == Coverage.FullProject) == 10 && snapshots.Count(s => s.Coverage == Coverage.PartialSelection) == 1, "Partial selection removed full snapshots."); });
        await tests.Run("Folder selection restores only the selected source with matching hashes", async () =>
        {
            var snapshot = (await app.Restic.SnapshotsAsync(fixture.Destination)).Single(s => s.Coverage == Coverage.PartialSelection); var target = Path.Combine(fixture.Root, "restored-partial");
            var progress = new ProgressRecorder(); await app.Restore.RestoreAsync(fixture.Destination, snapshot, target, progress: progress); progress.AssertSuccessfulRestore();
            Assert(progress.Items.Any(p => p.CurrentFile?.EndsWith("\\texture.txt") == true), "Selected folder did not report activity.");
            var file = Directory.EnumerateFiles(target, "texture.txt", SearchOption.AllDirectories).Single(); Assert(await Fixture.HashAsync(file) == await Fixture.HashAsync(Path.Combine(fixture.Main, "assets", "texture.txt")), "Partial selection hash mismatch.");
        });
        await tests.Run("An individual-file saved selection supports both whole-series and file restore", async () =>
        {
            var selected = new SavedSelection { ProjectId = fixture.Project.Id, Name = "Configuration file", Paths = [Path.Combine(fixture.Main, ".env")] };
            var job = (await app.Backups.RunAsync(fixture.Destination, [fixture.Project], selected)).Single(); Assert(job.Backup == Outcome.Successful, Json.Write(job));
            var snapshot = (await app.Restic.SnapshotsAsync(fixture.Destination)).Single(s => s.TagValue("series") == "selection-" + selected.Id);
            foreach (var mode in new[] { "whole", "file" })
            {
                var target = Path.Combine(fixture.Root, "restored-config-" + mode); await app.Restore.RestoreAsync(fixture.Destination, snapshot, target, mode == "file" ? PathSafety.SnapshotPath(selected.Paths[0]) : null);
                var file = Directory.EnumerateFiles(target, ".env", SearchOption.AllDirectories).Single(); Assert(await Fixture.HashAsync(file) == await Fixture.HashAsync(selected.Paths[0]), "Individual-selection restore mismatch.");
            }
        });
        await tests.Run("An unavailable root produces incomplete coverage and no cleanup", async () => { var project = fixture.Project with { Roots = [.. fixture.Project.Roots, new(Guid.NewGuid().ToString("N"), "Z:\\missing-" + Guid.NewGuid().ToString("N"))] }; app.Catalog.SaveProject(project); var result = (await app.Backups.RunAsync(fixture.Destination, [project])).Single(); Assert(result.Backup == Outcome.Incomplete && result.Cleanup == Outcome.NotRun, Json.Write(result)); Assert((await app.Restic.SnapshotsAsync(fixture.Destination)).Count(s => s.Successful && s.Coverage == Coverage.FullProject) == 10, "Incomplete run pruned prior versions."); app.Catalog.SaveProject(fixture.Project); });
        await tests.Run("Locked unreadable files are not reported as successful protection", async () => { using var locked = new FileStream(Path.Combine(fixture.Main, ".env"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); var result = (await app.Backups.RunAsync(fixture.Destination, [fixture.Project])).Single(); Assert(result.Backup == Outcome.Incomplete && result.Cleanup == Outcome.NotRun, Json.Write(result)); });
        await tests.Run("Cancellation records its result and preserves earlier recovery points", async () => { using var cancellation = new CancellationTokenSource(); var before = (await app.Restic.SnapshotsAsync(fixture.Destination)).Count; var progress = new ImmediateProgress(p => { if (p.Stage == "Preparing sources") cancellation.Cancel(); }); var result = (await app.Backups.RunAsync(fixture.Destination, [fixture.Project], progress: progress, token: cancellation.Token)).Single(); Assert(result.Backup == Outcome.Cancelled && (await app.Restic.SnapshotsAsync(fixture.Destination)).Count == before, "Cancellation status or protection incorrect."); });
        await tests.Run("Independent catalogs serialize operations on the same destination", async () => { using var lease = DestinationLease.Acquire(app.Paths, fixture.Destination.Path); var second = new AppPaths(Path.Combine(fixture.Root, "other-catalog")); await Throws<IOException>(() => { using var collision = DestinationLease.Acquire(second, fixture.Destination.Path); return Task.CompletedTask; }); });
        await tests.Run("A substituted repository identity is rejected and releases its destination lock", async () =>
        {
            await Throws<InvalidDataException>(() => app.Backups.RunAsync(fixture.Destination with { RepositoryId = "different-repository" }, [fixture.Project]));
            using var released = DestinationLease.Acquire(app.Paths, fixture.Destination.Path);
        });
        await tests.Run("An unavailable destination is recorded as failed without affecting earlier snapshots", async () =>
        {
            var before = (await app.Restic.SnapshotsAsync(fixture.Destination)).Count;
            var result = (await app.Backups.RunAsync(fixture.Destination with { Path = "Z:\\missing-repository-" + Guid.NewGuid().ToString("N") }, [fixture.Project])).Single();
            Assert(result.Backup == Outcome.Failed && result.Cleanup == Outcome.NotRun && app.Catalog.History().Any(h => h.Id == result.Id), "Destination failure was lost.");
            Assert((await app.Restic.SnapshotsAsync(fixture.Destination)).Count == before, "Destination failure removed earlier backups.");
        });
        await tests.Run("Repository verification reads and checks all stored data", async () => { var verification = await app.Backups.VerifyAsync(fixture.Destination); Assert(verification.Outcome == Outcome.Successful, verification.Message); });
        await tests.Run("Fresh computer recovery needs only the backup drive and the key when protected", async () =>
        {
            var expectedHead = await fixture.GitOutput(fixture.Main, "rev-parse", "HEAD");
            var expectedIndex = await fixture.GitOutput(fixture.Main, "show", ":staged.txt");
            var hashes = new Dictionary<string, string>();
            foreach (var relative in new[] { "README.md", "staged.txt", "untracked.txt", ".env", "assets\\texture.txt", "module\\module.txt" })
                hashes[relative] = await Fixture.HashAsync(Path.Combine(fixture.Main, relative));
            var copiedRepository = Path.Combine(fixture.Root, "replacement-drive", "backup");
            CopyRepository(fixture.Destination.Path, copiedRepository);
            string? recoveryKey = null;
            if (protection == DestinationProtection.RecoveryKey)
            {
                var keyFile = Path.Combine(fixture.Root, "exported-recovery.key");
                await app.Backups.ExportRecoveryKeyAsync(fixture.Destination, keyFile);
                recoveryKey = await File.ReadAllTextAsync(keyFile);
            }
            // Make both the original source paths and saved Windows credentials unavailable.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Move(Path.Combine(fixture.Root, "sources"), Path.Combine(fixture.Root, "offline-sources"));
            Directory.Move(app.Paths.DataDirectory, Path.Combine(fixture.Root, "offline-catalog"));
            var fresh = new ApplicationServices(Path.Combine(fixture.Root, "fresh-catalog"), discoveryProviders: []);
            var opened = await fresh.Backups.AddDestinationAsync("Recovered Backup", copiedRepository, recoveryKey, openExisting: true, protection: protection);
            Assert(await fresh.Restore.RebuildCatalogAsync(opened) == 1, "Project identity not recovered."); Assert(fresh.Catalog.Projects()[0].Id == fixture.Project.Id && !fresh.Catalog.Projects()[0].Enabled, "Recovered identity or review state incorrect."); Assert(fresh.Catalog.Selections().Any(s => s.Id == selection.Id), "Selection identity lost.");
            var latest = (await fresh.Restic.SnapshotsAsync(opened)).First(s => s.Successful && s.Coverage == Coverage.FullProject);
            var target = Path.Combine(fixture.Root, "fresh-restore"); await fresh.Restore.RestoreAsync(opened, latest, target);
            var manifest = await fresh.Restic.ManifestAsync(opened, latest);
            var restored = Path.Combine(target, "sources", manifest.Sources.Single(s => s.OriginalPath == fixture.Main).RestoreFolder);
            foreach (var (relative, hash) in hashes) Assert(await Fixture.HashAsync(Path.Combine(restored, relative)) == hash, "Fresh-computer hash mismatch: " + relative);
            Assert(await fixture.GitOutput(restored, "rev-parse", "HEAD") == expectedHead && await fixture.GitOutput(restored, "show", ":staged.txt") == expectedIndex, "Fresh-computer Git history or index changed.");
            Assert(fresh.Credentials.Exists(opened.Id) == (protection == DestinationProtection.RecoveryKey), "Fresh recovery used the wrong credential mode.");
        });
    }
    private static void CopyRepository(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
    }
    private sealed class ImmediateProgress(Action<BackupProgress> action) : IProgress<BackupProgress> { public void Report(BackupProgress value) => action(value); }
}
