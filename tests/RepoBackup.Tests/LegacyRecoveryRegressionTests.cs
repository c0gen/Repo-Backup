using System.Text;
using RepoBackup.Core.Application;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class LegacyRecoveryRegressionTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        foreach (var version in new[] { 1, 2, 3 })
        await tests.Run("Version " + version + " snapshots restore available data and diagnose missing sources", async () =>
        {
            var fixture = Path.Combine(root, version.ToString()); var app = new ApplicationServices(Path.Combine(fixture, "catalog"), discoveryProviders: []);
            var source = Path.Combine(fixture, "source"); Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "file.txt"), "legacy file");
            var project = app.Catalog.RegisterCandidate("Legacy", [source], "Fixture", approved: true);
            var mapping = new SourceMapping("source", source, PathSafety.SnapshotPath(source), "source", SourceKind.Project);
            var missing = Path.Combine(fixture, "missing");
            var manifest = new RecoveryManifest { SchemaVersion = version, JobId = Guid.NewGuid().ToString("N"), SeriesId = "project-" + project.Id, Project = project,
                Sources = [mapping, new("missing", missing, PathSafety.SnapshotPath(missing), "missing", SourceKind.Project)] };
            var manifestPath = Path.Combine(fixture, "manifest.json"); await File.WriteAllTextAsync(manifestPath, Json.Write(manifest));
            var destination = await app.Backups.AddDestinationAsync("Legacy", Path.Combine(fixture, "repository"), protection: DestinationProtection.PasswordFree);
            (await app.Restic.RunAsync(destination, ["backup", source, manifestPath, "--tag", "repobackup", "--tag", "successful", "--tag", "coverage=full", "--tag", "series=" + manifest.SeriesId,
                "--tag", "run=" + manifest.JobId, "--tag", "manifest=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(PathSafety.SnapshotPath(manifestPath)))])).EnsureSuccess("Write legacy fixture");
            var snapshot = (await app.Restic.SnapshotsAsync(destination)).Single();
            Assert((await app.Restic.ManifestAsync(destination, snapshot)).SchemaVersion == version, "Legacy version was not read.");
            var full = Path.Combine(fixture, "full-restore");
            try { await app.Restore.RestoreAsync(destination, snapshot, full); throw new InvalidOperationException("Missing legacy data claimed successful full recovery."); }
            catch (ArgumentException e) { Assert(e.Message.Contains("omitted data") && !Directory.Exists(full), "Missing source diagnostic was not actionable."); }
            var selected = Path.Combine(fixture, "available-restore"); await app.Restore.RestoreAsync(destination, snapshot, selected, mapping.SnapshotPath);
            Assert(await File.ReadAllTextAsync(Path.Combine(selected, "sources", "source", "file.txt")) == "legacy file", "Available legacy data was not recoverable.");
        });
        foreach (var version in new[] { 1, 2, 3 })
        await tests.Run("Version " + version + " extended Windows mappings restore through canonical snapshot paths", async () =>
        {
            var fixture = Path.Combine(root, "extended-" + version); var app = new ApplicationServices(Path.Combine(fixture, "catalog"), discoveryProviders: []);
            var source = Path.Combine(fixture, "source"); source = char.ToLowerInvariant(source[0]) + source[1..]; Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "file.txt"), "extended legacy data");
            var project = app.Catalog.RegisterCandidate("Extended", [source], "Fixture", approved: true);
            var extended = @"\\?\" + source;
            var mapping = new SourceMapping("source", extended, extended.Replace('\\', '/'), "source", SourceKind.Project);
            var manifest = new RecoveryManifest { SchemaVersion = version, JobId = Guid.NewGuid().ToString("N"), SeriesId = "project-" + project.Id, Project = project,
                Coverage = Coverage.FullProject, Sources = [mapping] };
            var file = Path.Combine(fixture, "manifest.json"); await File.WriteAllTextAsync(file, Json.Write(manifest));
            var destination = await app.Backups.AddDestinationAsync("Extended", Path.Combine(fixture, "repository"), protection: DestinationProtection.PasswordFree);
            (await app.Restic.RunAsync(destination, ["backup", source, file, "--tag", "repobackup", "--tag", "successful", "--tag", "coverage=full", "--tag", "series=" + manifest.SeriesId,
                "--tag", "run=" + manifest.JobId, "--tag", "manifest=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(PathSafety.SnapshotPath(file)))])).EnsureSuccess("Write extended legacy fixture");
            var snapshot = (await app.Restic.SnapshotSummariesAsync(destination)).Single();
            var target = Path.Combine(fixture, "restored"); await app.Restore.RestoreAsync(destination, snapshot, target);
            Assert(await File.ReadAllTextAsync(Path.Combine(target, "sources", "source", "file.txt")) == "extended legacy data", "Extended full restore failed.");
            await app.Restore.RestoreAsync(destination, snapshot, Path.Combine(fixture, "selected"), PathSafety.SnapshotPath(Path.Combine(source, "file.txt")));
        });
    }
}
