using RepoBackup.Core.Application;
using RepoBackup.Core.Git;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using RepoBackup.Core.Recovery;
using RepoBackup.Core.Windows;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class WslLinkBackupTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        foreach (var protection in new[] { DestinationProtection.PasswordFree, DestinationProtection.RecoveryKey })
            await RunModeAsync(tests, Path.Combine(root, protection.ToString()), protection);
        await tests.Run("Linked Git worktrees preserve WSL fixtures and ordinary dirty work", async () =>
        {
            var fixture = new Fixture(Path.Combine(root, "git")); await fixture.PrepareAsync();
            var link = Path.Combine(fixture.Worktree, "fixture-link"); WslLinkFixtures.Create(link, "/opt/absent");
            var destination = await fixture.App.Backups.AddDestinationAsync("WSL Git", Path.Combine(fixture.Root, "repository"));
            var job = (await fixture.App.Backups.RunAsync(destination, [fixture.Project])).Single();
            Assert(job.Backup == Outcome.Successful, Json.Write(job));
            var snapshot = (await fixture.App.Restic.SnapshotsAsync(destination)).Single();
            var manifest = await fixture.App.Restic.ManifestAsync(destination, snapshot);
            var target = Path.Combine(fixture.Root, "restored");
            await fixture.App.Restore.RestoreAsync(destination, snapshot, target);
            var mapping = manifest.Sources.Single(s => s.OriginalPath == fixture.Worktree);
            WslLinks.Verify(Path.Combine(target, "sources", mapping.RestoreFolder, "fixture-link"), WslLinks.Read(link)!);
            Assert(await File.ReadAllTextAsync(Path.Combine(target, "sources", mapping.RestoreFolder, "unfinished.txt")) == await File.ReadAllTextAsync(Path.Combine(fixture.Worktree, "unfinished.txt")), "Dirty worktree file changed.");
            Assert(fixture.OriginalHashes.SequenceEqual(await Fixture.HashesAsync(fixture.Main)), "Original project changed.");
        });
    }

    private static async Task RunModeAsync(TestRunner tests, string root, DestinationProtection protection)
    {
        Directory.CreateDirectory(root);
        var state = Path.Combine(root, "codex.json"); await File.WriteAllTextAsync(state, "{}");
        var app = new ApplicationServices(Path.Combine(root, "catalog"), codexStatePath: state);
        var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        var only = Path.Combine(source, "links-only"); Directory.CreateDirectory(only);
        await File.WriteAllTextAsync(Path.Combine(source, "ordinary.txt"), "ordinary data");
        var external = Path.Combine(root, "external"); Directory.CreateDirectory(external);
        await File.WriteAllTextAsync(Path.Combine(external, "external-only.txt"), "never follow");
        WslLinkFixtures.Create(Path.Combine(only, "legacy"), "../missing", 1);
        WslLinkFixtures.Create(Path.Combine(only, "linux"), "/opt/不存在/target");
        WslLinkFixtures.Create(Path.Combine(only, "cycle"), "cycle");
        WslLinkFixtures.Create(Path.Combine(source, "external-link"), external.Replace('\\', '/'));
        var project = app.Catalog.RegisterCandidate("Linux link project", [source], "Fixture", approved: true);
        var destination = await app.Backups.AddDestinationAsync("WSL destination", Path.Combine(root, "repository"), protection: protection);
        SnapshotInfo snapshot = null!;
        RecoveryManifest manifest = null!;

        await tests.Run($"WSL links survive verified backup and full restore ({protection})", async () =>
        {
            var job = (await app.Backups.RunAsync(destination, [project])).Single();
            Assert(job.Backup == Outcome.Successful && job.Verification == Outcome.Successful, Json.Write(job));
            snapshot = (await app.Restic.SnapshotsAsync(destination)).Single();
            manifest = await app.Restic.ManifestAsync(destination, snapshot);
            Assert(manifest.SchemaVersion == 4 && manifest.WslLinks.Count == 4, "Missing versioned link metadata.");
            var raw = await app.Restic.FilesAsync(destination, snapshot.Id);
            Assert(!raw.Any(n => n.Path.EndsWith("external-link") || n.Path.EndsWith("external-only.txt")), "Native backup received a WSL link or followed its target.");
            var logical = await SnapshotContents.ListAsync(app.Restic, destination, snapshot.Id);
            Assert(logical.Count(n => n.Type == "symlink") == 4 && logical.Any(n => n.Path == PathSafety.SnapshotPath(only) && n.Type == "dir"), "Browse tree omitted virtual entries.");
            var displayed = RepoBackup.Desktop.ViewModels.SnapshotBrowser.ProjectFiles(logical, manifest).ToList();
            Assert(displayed.Count(n => n.Type == "Link") == 4 && displayed.All(n => !n.Path.Contains("manifest.json")), "Desktop browsing omitted links or exposed internal files.");
            var cli = await NativeProcessFixture.RunAsync(NativeProcessFixture.ProgramPath("RepoBackup.Cli"),
                ["files", "--destination", destination.Id, "--snapshot", snapshot.Id, "--data-dir", app.Paths.DataDirectory], timeoutSeconds: 60);
            cli.EnsureSuccess("List WSL links through CLI");
            Assert(Json.Read<List<SnapshotFile>>(cli.Output).Count(n => n.Type == "symlink") == 4, "CLI listing omitted WSL links.");
            var target = Path.Combine(root, "restored"); await app.Restore.RestoreAsync(destination, snapshot, target);
            var mapping = manifest.Sources.Single();
            foreach (var link in manifest.WslLinks)
                WslLinks.Verify(Path.Combine(target, "sources", mapping.RestoreFolder, link.RelativePath.Replace('/', '\\')), link.Data);
            Assert(await File.ReadAllTextAsync(Path.Combine(target, "sources", mapping.RestoreFolder, "ordinary.txt")) == "ordinary data", "Ordinary file changed.");
            Assert(await File.ReadAllTextAsync(Path.Combine(external, "external-only.txt")) == "never follow", "External target changed.");
        });
        await tests.Run($"Individual links, link-only folders and link-only sources restore ({protection})", async () =>
        {
            var link = manifest.WslLinks.Single(l => l.RelativePath == "links-only/legacy");
            var single = Path.Combine(root, "restore-one");
            await app.Restore.RestoreAsync(destination, snapshot, single, PathSafety.SnapshotPath(Path.Combine(only, "legacy")));
            var restoredSource = Path.Combine(single, "sources", manifest.Sources.Single().RestoreFolder);
            WslLinks.Verify(Path.Combine(restoredSource, "links-only", "legacy"), link.Data);
            Assert(!File.Exists(Path.Combine(restoredSource, "ordinary.txt")), "Selective restore restored siblings.");
            var folder = Path.Combine(root, "restore-folder");
            await app.Restore.RestoreAsync(destination, snapshot, folder, PathSafety.SnapshotPath(only));
            Assert(Directory.GetFileSystemEntries(Path.Combine(folder, "sources", manifest.Sources.Single().RestoreFolder, "links-only")).Length == 3, "Link-only directory not restored.");
            foreach (var path in new[] { only, Path.Combine(only, "legacy") })
            {
                var selection = new SavedSelection { ProjectId = project.Id, Name = "link selection", Paths = [path] };
                var job = (await app.Backups.RunAsync(destination, [project], selection)).Single();
                Assert(job.Backup == Outcome.Successful, Json.Write(job));
                var selected = (await app.Restic.SnapshotsAsync(destination)).Single(s => s.TagValue("run") == job.Id);
                var selectedManifest = await app.Restic.ManifestAsync(destination, selected);
                var target = Path.Combine(root, "selected-" + job.Id);
                await app.Restore.RestoreAsync(destination, selected, target);
                var mapping = selectedManifest.Sources.Single();
                var expected = selectedManifest.WslLinks.First();
                var relative = expected.RelativePath == "." ? Path.GetFileName(path) : expected.RelativePath;
                WslLinks.Verify(Path.Combine(target, "sources", mapping.RestoreFolder, relative.Replace('/', '\\')), expected.Data);
            }
        });
        await tests.Run($"Fresh catalogs recover WSL links using only repository metadata ({protection})", async () =>
        {
            var fresh = new ApplicationServices(Path.Combine(root, "fresh"), codexStatePath: state);
            var recovered = await fresh.Backups.AddDestinationAsync("Recovered", destination.Path,
                protection == DestinationProtection.RecoveryKey ? app.Credentials.Get(destination.Id) : null, openExisting: true, protection: protection);
            Assert(await fresh.Restore.RebuildCatalogAsync(recovered) >= 1, "Catalog recovery omitted v2 snapshots.");
            var target = Path.Combine(root, "fresh-restore"); await fresh.Restore.RestoreAsync(recovered, snapshot, target);
            WslLinks.Verify(Path.Combine(target, "sources", manifest.Sources.Single().RestoreFolder, "links-only", "linux"), manifest.WslLinks.Single(l => l.RelativePath == "links-only/linux").Data);
        });
        await tests.Run($"WSL restore I/O failures cannot report successful recovery ({protection})", async () =>
        {
            // Exercise restore failure bookkeeping with native permission / unsupported-filesystem errors.
            foreach (var error in new[] { 5, 50 })
            {
                var restore = new RestoreService(app.Paths, app.Catalog, app.Restic, new GitRecovery(new GitInspector(new ProcessRunner())),
                    (_, _, _) => throw new IOException(new System.ComponentModel.Win32Exception(error).Message));
                var previous = Json.Write(app.Catalog.GetSetting<System.Text.Json.JsonElement>("lastRestore"));
                var target = Path.Combine(root, "failed-" + error);
                await Throws<IOException>(() => restore.RestoreAsync(destination, snapshot, target));
                Assert(File.Exists(Path.Combine(target, "RESTORE-INCOMPLETE.txt")) && !File.Exists(Path.Combine(target, "RESTORED.txt")), "Failed link restore claimed success.");
                Assert(Json.Write(app.Catalog.GetSetting<System.Text.Json.JsonElement>("lastRestore")) == previous, "Failed restore changed successful history.");
            }
        });
        await tests.Run($"Live link changes prevent completion and retention cleanup ({protection})", async () =>
        {
            var progress = new CallbackProgress(p =>
            {
                if (p.Stage != "Checking live coverage") return;
                var path = Path.Combine(only, "cycle"); File.Delete(path); WslLinkFixtures.Create(path, "retargeted");
            });
            var job = (await app.Backups.RunAsync(destination, [project], progress: progress)).Single();
            Assert(job.Backup == Outcome.Incomplete && job.Cleanup == Outcome.NotRun, Json.Write(job));
            Assert((await app.Restic.SnapshotsAsync(destination)).Any(s => s.Id == snapshot.Id && s.Successful), "Prior recovery point lost.");
        });
    }

    private sealed class CallbackProgress(Action<BackupProgress> callback) : IProgress<BackupProgress>
    { public void Report(BackupProgress value) => callback(value); }
}
