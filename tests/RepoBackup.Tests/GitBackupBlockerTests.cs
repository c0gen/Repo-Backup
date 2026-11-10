using RepoBackup.Core.Application;
using RepoBackup.Core.Backup;
using RepoBackup.Core.Git;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using System.Text;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class GitBackupBlockerTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        await tests.Run("Worktree parser preserves complete foreign, locked and prunable records", () =>
        {
            var records = GitInspector.ParseWorktrees("worktree /private/tmp/with\nnewline\0HEAD abc\0detached\0prunable missing gitdir\0\0worktree Z:/offline\0locked keep\0\0");
            Assert(records.Count == 2 && records[0].Path == "/private/tmp/with\nnewline" && records[0].PrunableReason == "missing gitdir"
                && records[1].Locked && records[1].PrunableReason is null, "Worktree record boundaries or flags were lost.");
            return Task.CompletedTask;
        });
        foreach (var protection in Enum.GetValues<DestinationProtection>())
            await RecoveryAsync(tests, Path.Combine(root, protection.ToString()), protection);
        await tests.Run("Broken worktree and alternates do not hide a later submodule or object store", async () =>
        {
            var fixture = new Fixture(Path.Combine(root, "isolated-errors")); await fixture.PrepareAsync();
            File.SetAttributes(Path.Combine(fixture.Worktree, ".git"), FileAttributes.Normal);
            await File.WriteAllTextAsync(Path.Combine(fixture.Worktree, ".git"), "gitdir: missing-admin\n");
            File.SetAttributes(Path.Combine(fixture.OtherWorktree, ".git"), FileAttributes.Normal);
            File.Delete(Path.Combine(fixture.OtherWorktree, ".git"));
            var alternate = Path.Combine(fixture.Main, ".git", "objects", "info", "alternates");
            var available = Path.Combine(root, "available-store", "objects"); Directory.CreateDirectory(available);
            await File.WriteAllLinesAsync(alternate, [Path.Combine(root, "absent-store", "objects"), available]);
            var inventory = await new GitInspector(new ProcessRunner()).InventoryAsync([fixture.Main]);
            Assert(inventory.Issues.Any(i => i.Code == "git.metadata-unavailable")
                && inventory.Issues.Any(i => i.Code == "git.metadata-unavailable" && i.Path == fixture.OtherWorktree)
                && inventory.Issues.Any(i => i.Code == "git.alternate-unavailable")
                && inventory.Contexts.Any(c => c.WorkingPath == Path.Combine(fixture.Main, "module"))
                && inventory.Sources.Any(s => s.Path == Path.GetDirectoryName(available)), "An earlier broken entry hid available local data.");
        });
        await LongGitPathTests.RunAsync(tests, root);
    }

    private static async Task RecoveryAsync(TestRunner tests, string root, DestinationProtection protection)
    {
        var fixture = new Fixture(root); await fixture.PrepareAsync();
        var old = fixture.Main; var moved = Path.Combine(root, "moved-main");
        var orphan = Path.Combine(old, ".git", "worktrees", "foreign-orphan"); Directory.CreateDirectory(orphan);
        Directory.CreateDirectory(Path.Combine(orphan, "refs"));
        await File.WriteAllTextAsync(Path.Combine(orphan, "gitdir"), "/private/tmp/old-machine/.git\n");
        await File.WriteAllTextAsync(Path.Combine(orphan, "commondir"), "../..\n");
        var head = (await fixture.GitOutput(old, "rev-parse", "HEAD")).Trim();
        await File.WriteAllTextAsync(Path.Combine(orphan, "HEAD"), head + "\n");
        Assert(PathSafety.IsWithin(old, root) && PathSafety.IsWithin(moved, root), "Fixture move escapes its directory.");
        Directory.Move(old, moved); fixture.App.Catalog.RelinkRoot(fixture.Project.Id, fixture.Project.Roots[0].Id, moved);
        var project = fixture.App.Catalog.Projects().Single();
        // A tracked default-excluded path in a broken linked worktree must still be protected.
        var tracked = Path.Combine(fixture.Worktree, "node_modules", "tracked.txt");
        var original = await Fixture.HashesAsync(moved); var worktreeBefore = await Fixture.HashesAsync(fixture.Worktree);
        var destination = await fixture.App.Backups.AddDestinationAsync("Blocker fixture", Path.Combine(root, "repository"), protection: protection);
        JobRecord job = null!; SnapshotInfo snapshot = null!;
        await tests.Run(protection + ": moved worktrees and foreign registrations create verified complete backups", async () =>
        {
            var preview = await fixture.App.Planner.PreviewAsync(project);
            Assert(preview.Complete && preview.Files.Any(f => f.Path == tracked) && preview.GitContexts.Any(c => c.WorkingPath == fixture.Worktree)
                && preview.Issues.Any(i => i.Code == "git.stale-worktree" && i.Severity == BackupIssueSeverity.Advisory), Json.Write(preview.Issues));
            var selectedProject = project with { Roots = [.. project.Roots, new("selected-worktree", fixture.Worktree)] };
            var selected = SavedSelection.FromProject(selectedProject, "Tracked dependency", [Path.GetDirectoryName(tracked)!]);
            var selectedPreview = await fixture.App.Planner.PreviewAsync(selectedProject, selected);
            Assert(selectedPreview.Complete && selectedPreview.Files.Any(f => f.Path == tracked), "Moved worktree selection lost tracked-file protection.");
            job = (await fixture.App.Backups.RunAsync(destination, [project])).Single();
            Assert(RunOutcome.IsSuccessful(job) && job.Issues.All(i => i.Severity == BackupIssueSeverity.Advisory) && job.SourceChanges is { HasChanges: false }, Json.Write(job));
            snapshot = (await fixture.App.Restic.SnapshotsAsync(destination)).Single();
            var manifest = await fixture.App.Restic.ManifestAsync(destination, snapshot);
            Assert(manifest.SchemaVersion == 4 && manifest.GitContexts.Any(c => c.WorkingPath == fixture.Worktree)
                && manifest.GitRegistrations.Any(r => !r.Available), "Resolved relationships were not recorded.");
            var summaries = await fixture.App.Restic.SnapshotSummariesAsync(destination);
            Assert(summaries.Single().Id == snapshot.Id && snapshot.Paths.Length > 0, "Summary/full-path APIs disagree.");
        });
        await tests.Run(protection + ": restored moved worktrees are internal and orphan metadata is preserved", async () =>
        {
            if (snapshot is null) throw new InvalidOperationException("Backup prerequisite failed.");
            var target = Path.Combine(root, "restored");
            await fixture.App.Restore.RestoreAsync(destination, snapshot, target);
            var manifest = await fixture.App.Restic.ManifestAsync(destination, snapshot);
            var main = Path.Combine(target, "sources", manifest.Sources.Single(s => s.OriginalPath == moved).RestoreFolder);
            var worktree = Path.Combine(target, "sources", manifest.Sources.Single(s => s.OriginalPath == fixture.Worktree).RestoreFolder);
            Assert(await File.ReadAllTextAsync(Path.Combine(worktree, "node_modules", "tracked.txt")) == await File.ReadAllTextAsync(tracked), "Tracked exclusion protection was lost.");
            Assert((await fixture.GitOutput(main, "worktree", "list", "--porcelain")).Contains(worktree.Replace('\\', '/')), "Restored worktree relationship is missing.");
            Assert(Directory.GetDirectories(Path.Combine(target, "git-registration-archive")).Length == 1
                && (await fixture.GitOutput(main, "show-ref")).Contains("refs/repobackup/orphan-worktrees/"), "Orphan registration or commit was lost.");
            Assert(Directory.Exists(Path.Combine(Directory.GetDirectories(Path.Combine(target, "git-registration-archive")).Single(), "refs")),
                "Captured empty metadata directory was not preserved in the orphan archive.");
            var after = await Fixture.HashesAsync(moved); var worktreeAfter = await Fixture.HashesAsync(fixture.Worktree);
            Assert(original.Count == after.Count && original.All(p => after.GetValueOrDefault(p.Key) == p.Value)
                && worktreeBefore.Count == worktreeAfter.Count && worktreeBefore.All(p => worktreeAfter.GetValueOrDefault(p.Key) == p.Value), "Backup/recovery modified original Git files.");
        });
        await tests.Run(protection + ": locked unavailable worktrees remain coverage errors", async () =>
        {
            await File.WriteAllTextAsync(Path.Combine(moved, ".git", "worktrees", "foreign-orphan", "locked"), "Keep unavailable worktree\n");
            var preview = await fixture.App.Planner.PreviewAsync(project);
            Assert(!preview.Complete && preview.Issues.Any(i => i.Code == "git.worktree-unavailable" && i.Severity == BackupIssueSeverity.CoverageError), "Locked unavailable worktree was silently discarded.");
        });
        foreach (var version in new[] { 1, 2, 3 })
        await tests.Run(protection + ": legacy version " + version + " restores moved and foreign worktrees from captured metadata", async () =>
        {
            var current = await fixture.App.Restic.ManifestAsync(destination, snapshot);
            var legacy = current with { SchemaVersion = version, JobId = Guid.NewGuid().ToString("N"), GitContexts = [], GitRegistrations = [], Issues = [], Warnings = [],
                Directories = version < 3 ? [] : current.Directories, GitMetadataDirectories = version < 3 ? [] : current.GitMetadataDirectories };
            var file = Path.Combine(root, "legacy-" + version + ".json"); await File.WriteAllTextAsync(file, Json.Write(legacy));
            var args = new List<string> { "backup", "--tag", "repobackup", "--tag", "successful", "--tag", "coverage=full", "--tag", "series=" + legacy.SeriesId,
                "--tag", "run=" + legacy.JobId, "--tag", "manifest=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(PathSafety.SnapshotPath(file))), file };
            args.AddRange(current.Sources.Select(s => s.OriginalPath));
            (await fixture.App.Restic.RunAsync(destination, args)).EnsureSuccess("Write legacy Git fixture");
            var oldSnapshot = (await fixture.App.Restic.SnapshotSummariesAsync(destination)).Single(s => s.TagValue("run") == legacy.JobId);
            var target = Path.Combine(root, "legacy-restored-" + version);
            await fixture.App.Restore.RestoreAsync(destination, oldSnapshot, target);
            Assert(Directory.Exists(Path.Combine(target, "git-registration-archive"))
                && File.Exists(Path.Combine(target, "sources", current.Sources.Single(s => s.OriginalPath == fixture.Worktree).RestoreFolder, "unfinished.txt")), "Legacy worktree recovery failed.");
        });
        await tests.Run(protection + ": ambiguous moved metadata is a coverage error without aborting later worktrees", async () =>
        {
            var adminRoot = Path.Combine(moved, ".git", "worktrees");
            var originalAdmin = Directory.GetDirectories(adminRoot).Single(p => File.Exists(Path.Combine(p, "gitdir"))
                && File.ReadAllText(Path.Combine(p, "gitdir")).Trim().Equals(Path.Combine(fixture.Worktree, ".git").Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
            var ambiguous = Path.Combine(adminRoot, "ambiguous"); Directory.CreateDirectory(ambiguous);
            foreach (var name in new[] { "gitdir", "commondir", "HEAD" }) File.Copy(Path.Combine(originalAdmin, name), Path.Combine(ambiguous, name));
            var preview = await fixture.App.Planner.PreviewAsync(project);
            Assert(!preview.Complete && preview.Issues.Any(i => i.Message.Contains("ambiguous", StringComparison.OrdinalIgnoreCase))
                && preview.GitContexts.Any(c => c.WorkingPath == fixture.OtherWorktree), "Ambiguous worktree was guessed or blocked subsequent inspection.");
        });
        await tests.Run(protection + ": an offline metadata volume cannot be mistaken for a moved repository", async () =>
        {
            var assigned = DriveInfo.GetDrives().Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var drive = Enumerable.Range('H', 'Z' - 'H' + 1).Select(c => (char)c + @":\").First(p => !assigned.Contains(p));
            var pointer = Path.Combine(fixture.Worktree, ".git");
            var adminId = Path.GetFileName((await File.ReadAllTextAsync(pointer)).Trim()[7..].Trim());
            File.SetAttributes(pointer, FileAttributes.Normal);
            await File.WriteAllTextAsync(pointer, "gitdir: " + Path.Combine(drive, "unavailable", ".git", "worktrees", adminId) + "\n");
            var preview = await fixture.App.Planner.PreviewAsync(project);
            Assert(!preview.Complete && preview.Issues.Any(i => i.Code == "git.metadata-unavailable" && i.Path == fixture.Worktree
                && i.Message.Contains("drive or share is unavailable")) && preview.GitContexts.Any(c => c.WorkingPath == fixture.OtherWorktree),
                "Offline metadata was treated as confirmed relocation or stopped later inspection.");
            var incomplete = (await fixture.App.Backups.RunAsync(destination, [project])).Single();
            Assert(incomplete.Backup == Outcome.Incomplete, Json.Write(incomplete));
            var captured = (await fixture.App.Restic.SnapshotSummariesAsync(destination)).Single(s => s.Id == incomplete.SnapshotId);
            try
            {
                await fixture.App.Restore.RestoreAsync(destination, captured, Path.Combine(root, "unresolved-restored"));
                throw new InvalidOperationException("Recovery guessed an unresolved Git relationship.");
            }
            catch (InvalidDataException error) { Assert(error.Message.Contains("no resolved Git relationship"), error.Message); }
        });
    }
}
