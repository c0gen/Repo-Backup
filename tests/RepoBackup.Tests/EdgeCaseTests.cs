using RepoBackup.Core.Application;
using RepoBackup.Core.Backup;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class EdgeCaseTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        var state = Path.Combine(root, "empty-codex-state.json"); Directory.CreateDirectory(root); await File.WriteAllTextAsync(state, "{\"local-projects\":{}}");
        var app = new ApplicationServices(Path.Combine(root, "catalog"), codexStatePath: state);
        var source = Path.Combine(root, "source"); var external = Path.Combine(root, "external");
        Directory.CreateDirectory(source); Directory.CreateDirectory(external);
        await File.WriteAllTextAsync(Path.Combine(source, "working.txt"), "Original working data");
        await File.WriteAllTextAsync(Path.Combine(external, "external-only.txt"), "Never traverse this external target");
        var project = app.Catalog.RegisterCandidate("Link fixture", [source], "Fixture", approved: true);
        var destination = await app.Backups.AddDestinationAsync("Edge-case repository", Path.Combine(root, "repository"));
        await tests.Run("Directory junctions are stored as links without backing up external targets", async () =>
        {
            var junction = Path.Combine(source, "external-link");
            string Quote(string value) => "'" + value.Replace("'", "''") + "'";
            (await new ProcessRunner().RunAsync("powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "New-Item -ItemType Junction -Path " + Quote(junction) + " -Target " + Quote(external) + " -ErrorAction Stop | Out-Null"])).EnsureSuccess("Create fixture junction");
            var preview = await app.Planner.PreviewAsync(project);
            Assert(preview.Complete && preview.Files.Any(f => f.Path == junction && f.IsLink), "Preview did not recognize the junction.");
            Assert(!preview.Files.Any(f => f.Path.EndsWith("external-only.txt")), "Preview traversed the external target.");
            var job = (await app.Backups.RunAsync(destination, [project])).Single(); Assert(job.Backup == Outcome.Successful, Json.Write(job));
            var snapshot = (await app.Restic.SnapshotsAsync(destination)).Single();
            var files = await app.Restic.FilesAsync(destination, snapshot.Id);
            Assert(files.Single(f => f.Path == PathSafety.SnapshotPath(junction)).Type == "symlink" && !files.Any(f => f.Path.EndsWith("external-only.txt")), "Restic followed the junction.");
        });
        await tests.Run("Insufficient-space rejection preserves existing snapshots and records a failed run", async () =>
        {
            var before = (await app.Restic.SnapshotsAsync(destination)).Select(s => s.Id).ToArray();
            var lowSpace = new BackupService(app.Paths, app.Catalog, app.Credentials, app.Restic, app.Planner, app.Discovery, availableSpace: _ => 0);
            var result = (await lowSpace.RunAsync(destination, [project])).Single();
            Assert(result.Backup == Outcome.Failed && result.Cleanup == Outcome.NotRun && result.Messages.Any(m => m.Contains("Insufficient free space")), Json.Write(result));
            Assert(before.SequenceEqual((await app.Restic.SnapshotsAsync(destination)).Select(s => s.Id)), "Insufficient-space failure changed earlier snapshots.");
        });
        await tests.Run("Observed file changes during live backup cause incomplete coverage and no cleanup", async () =>
        {
            var progress = new ImmediateProgress(p => { if (p.Stage == "Checking live coverage") File.AppendAllText(Path.Combine(source, "working.txt"), "Changed while backup was running"); });
            var result = (await app.Backups.RunAsync(destination, [project], progress: progress)).Single();
            Assert(result.Backup == Outcome.Incomplete && result.Cleanup == Outcome.NotRun, Json.Write(result));
            Assert((await app.Restic.SnapshotsAsync(destination)).Count(s => s.Successful) == 1, "A changing live source was marked successful.");
        });
        await tests.Run("External LFS storage and shared object alternates recover inside the new directory", async () =>
        {
            var fixture = new Fixture(Path.Combine(root, "external-git")); await fixture.PrepareAsync();
            var common = Path.Combine(fixture.Root, "shared-repository"); var clone = Path.Combine(fixture.Root, "shared-clone");
            await fixture.Git(fixture.Main, "clone", "--bare", fixture.Main, common);
            await fixture.Git(fixture.Main, "clone", "--shared", common, clone);
            var lfs = Path.Combine(fixture.Root, "lfs-storage"); Directory.CreateDirectory(lfs); await File.WriteAllTextAsync(Path.Combine(lfs, "local-object"), "Local LFS object bytes");
            await fixture.Git(clone, "config", "lfs.storage", lfs.Replace('\\', '/'));
            var entry = fixture.App.Catalog.RegisterCandidate("Shared Git fixture", [clone], "Fixture", approved: true);
            var preview = await fixture.App.Planner.PreviewAsync(entry); Assert(preview.Complete && preview.Sources.Count == 3, Json.Write(preview.Sources) + string.Join("; ", preview.Warnings));
            var repo = await fixture.App.Backups.AddDestinationAsync("Shared Git repository", Path.Combine(fixture.Root, "repository"));
            var job = (await fixture.App.Backups.RunAsync(repo, [entry])).Single(); Assert(job.Backup == Outcome.Successful, Json.Write(job));
            var snapshot = (await fixture.App.Restic.SnapshotsAsync(repo)).Single(); var target = Path.Combine(fixture.Root, "restored");
            await fixture.App.Restore.RestoreAsync(repo, snapshot, target);
            var manifest = await fixture.App.Restic.ManifestAsync(repo, snapshot); var restored = Path.Combine(target, "sources", manifest.Sources.Single(s => s.OriginalPath == clone).RestoreFolder);
            var restoredLfs = (await fixture.GitOutput(restored, "config", "--path", "--get", "lfs.storage")).Trim();
            Assert(PathSafety.IsWithin(restoredLfs, target) && await Fixture.HashAsync(Path.Combine(restoredLfs, "local-object")) == await Fixture.HashAsync(Path.Combine(lfs, "local-object")), "External LFS data or path was not restored.");
            var alternate = (await File.ReadAllTextAsync(Path.Combine(restored, ".git", "objects", "info", "alternates"))).Trim();
            Assert(PathSafety.IsWithin(alternate, target), "Restored alternate still points at the original repository.");
            Assert(await fixture.GitOutput(restored, "rev-parse", "HEAD") == await fixture.GitOutput(clone, "rev-parse", "HEAD"), "Shared-clone commits changed.");
        });
        await tests.Run("Submodules with external Git databases restore all local history without original writes", async () =>
        {
            var fixture = new Fixture(Path.Combine(root, "external-submodule")); await fixture.PrepareAsync();
            var oldDatabase = Path.Combine(fixture.Main, ".git", "modules", "module"); var database = Path.Combine(fixture.Root, "separate-module-database");
            var module = Path.Combine(fixture.Main, "module"); var pointer = Path.Combine(module, ".git");
            await fixture.Git(module, "config", "core.worktree", module.Replace('\\', '/'));
            Assert(PathSafety.IsWithin(oldDatabase, fixture.Root) && PathSafety.IsWithin(database, fixture.Root), "Fixture move must stay in its workspace.");
            Directory.Move(oldDatabase, database);
            File.SetAttributes(pointer, FileAttributes.Normal); await File.WriteAllTextAsync(pointer, "gitdir: " + database.Replace('\\', '/') + "\n");
            var original = await Fixture.HashesAsync(fixture.Main); var originalDatabase = await Fixture.HashesAsync(database);
            var preview = await fixture.App.Planner.PreviewAsync(fixture.Project); Assert(preview.Complete && preview.Sources.Any(s => s.OriginalPath == database), "External submodule database was omitted: " + string.Join("; ", preview.Warnings));
            var repo = await fixture.App.Backups.AddDestinationAsync("External submodule repository", Path.Combine(fixture.Root, "repository"));
            var job = (await fixture.App.Backups.RunAsync(repo, [fixture.Project])).Single(); Assert(job.Backup == Outcome.Successful, Json.Write(job));
            var snapshot = (await fixture.App.Restic.SnapshotsAsync(repo)).Single(); var target = Path.Combine(fixture.Root, "restored");
            await fixture.App.Restore.RestoreAsync(repo, snapshot, target);
            var manifest = await fixture.App.Restic.ManifestAsync(repo, snapshot); var main = Path.Combine(target, "sources", manifest.Sources.Single(s => s.OriginalPath == fixture.Main).RestoreFolder);
            var restoredDatabase = (await fixture.GitOutput(Path.Combine(main, "module"), "rev-parse", "--absolute-git-dir")).Trim();
            Assert(PathSafety.IsWithin(restoredDatabase, target), "Submodule still references the original external database.");
            Assert(await fixture.GitOutput(Path.Combine(main, "module"), "rev-parse", "HEAD") == await fixture.GitOutput(module, "rev-parse", "HEAD"), "Submodule history changed.");
            Assert(original.SequenceEqual(await Fixture.HashesAsync(fixture.Main)) && originalDatabase.SequenceEqual(await Fixture.HashesAsync(database)), "Recovery modified original files or Git metadata.");
        });
    }
    private sealed class ImmediateProgress(Action<BackupProgress> action) : IProgress<BackupProgress> { public void Report(BackupProgress value) => action(value); }
}
