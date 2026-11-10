using RepoBackup.Core.Application;
using RepoBackup.Core.Backup;
using RepoBackup.Core.Models;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class CoverageDiagnosticTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        await tests.Run("Identical inventories with advisories have no changes and complete coverage", () =>
        {
            var before = new Preview([], [new(Path.Combine(root, "file.txt"), 1, 1)], [], [], Coverage.FullProject)
                { Issues = [new("git.stale-worktree", BackupIssueSeverity.Advisory, "/private/tmp/stale", "Stale worktree")] };
            var changes = SourceChanges.Compare(before, before);
            Assert(before.Complete && !changes.HasChanges && changes.Samples.Count == 0, "An advisory was interpreted as a change or missing data.");
            var blocked = before with { Issues = [new("source.access-denied", BackupIssueSeverity.CoverageError, before.Files[0].Path, "Access denied")] };
            Assert(!blocked.Complete && !SourceChanges.Compare(blocked, blocked).HasChanges, "A stable access problem was misreported as changing files.");
            return Task.CompletedTask;
        });
        await tests.Run("Source change diagnostics count additions, removals, edits and metadata with bounded samples", () =>
        {
            var before = new Preview([], [new(Path.Combine(root, "removed"), 1, 1), new(Path.Combine(root, "edited"), 1, 1)], [], [], Coverage.FullProject);
            var after = before with { Files = [new(Path.Combine(root, "edited"), 2, 2), .. Enumerable.Range(0, 150).Select(i => new FileEntry(Path.Combine(root, "new-" + i), 1, 1))],
                GitMetadataDirectories = [Path.Combine(root, ".git")] };
            var changes = SourceChanges.Compare(before, after);
            Assert(changes.Added == 150 && changes.Removed == 1 && changes.Modified == 1 && changes.MetadataChanged == 1 && changes.Samples.Count == 100, "Incorrect change summary or unbounded samples.");
            return Task.CompletedTask;
        });
        await tests.Run("VSS tolerates ordinary content edits but cannot hide changes to exact-file selection", () =>
        {
            var before = new Preview([], [new(Path.Combine(root, "same"), 1, 1), new(Path.Combine(root, "removed"), 1, 1)], [], [], Coverage.FullProject);
            var edited = before with { Files = [before.Files[0] with { Length = 2, ModifiedTicks = 2 }, before.Files[1]] };
            Assert(!SourceChanges.Compare(before, edited, metadataOnly: true).HasChanges, "An atomic VSS content edit was reported as a selection change.");
            var after = edited with { Files = [edited.Files[0], new(Path.Combine(root, "added-git-object"), 1, 1)] };
            var changes = SourceChanges.Compare(before, after, metadataOnly: true);
            Assert(changes.Added == 1 && changes.Removed == 1 && changes.Modified == 0, "VSS hid files added to or removed from the prepared selection.");
            var type = before with { Files = [before.Files[0] with { IsLink = true }, before.Files[1]] };
            Assert(SourceChanges.Compare(before, type, metadataOnly: true).Modified == 1, "VSS hid a file changing into a link.");
            return Task.CompletedTask;
        });
        await tests.Run("Alias retention combines equivalent full histories only after verification", () =>
        {
            var project = new ProjectEntry { Id = "current", LegacyIds = ["old"] };
            var now = DateTimeOffset.UtcNow;
            var snapshots = Enumerable.Range(0, 12).Select(i => new SnapshotSummary("full-" + i, now.AddMinutes(-i),
                ["successful", "coverage=full", "series=project-" + (i % 2 == 0 ? "current" : "old")])).ToList();
            snapshots.Add(new("incomplete", now.AddYears(-1), ["incomplete", "coverage=full", "series=project-old"]));
            snapshots.Add(new("pending", now.AddYears(-1), ["successful", "pending", "coverage=full", "series=project-old"]));
            snapshots.AddRange(Enumerable.Range(0, 12).Select(i => new SnapshotSummary("selection-" + i, now.AddMinutes(-i),
                ["successful", "coverage=partial", "series=selection-id"])));
            var job = new JobRecord { ProjectId = project.Id, ProjectName = "Aliases", SeriesId = "project-current", DestinationId = "fixture",
                Coverage = Coverage.FullProject, Backup = Outcome.Successful, Verification = Outcome.Successful };
            var obsolete = SnapshotRetention.Obsolete(snapshots, [job], [project]);
            Assert(obsolete.Order().SequenceEqual(new[] { "full-10", "full-11" }), "Retention crossed coverage, pending or selection boundaries.");
            Assert(SnapshotRetention.Obsolete(snapshots, [job with { Verification = Outcome.Failed }], [project]).Count == 0,
                "Unverified backup triggered alias retention.");
            Assert(SnapshotRetention.Obsolete(snapshots, [job with { Coverage = Coverage.PartialSelection, SeriesId = "selection-id" }], [project])
                .Order().SequenceEqual(new[] { "selection-10", "selection-11" }), "Selection series lost independent retention.");
            return Task.CompletedTask;
        });
        await tests.Run("Missing selected roots report their path and relink action while available data remains", async () =>
        {
            var app = new ApplicationServices(Path.Combine(root, "catalog"), discoveryProviders: []);
            var source = Path.Combine(root, "available"); Directory.CreateDirectory(source); await File.WriteAllTextAsync(Path.Combine(source, "file.txt"), "available");
            var missing = Path.Combine(root, "missing");
            var project = app.Catalog.RegisterCandidate("Mixed", [source, missing], "Manual", approved: true);
            var preview = await app.Planner.PreviewAsync(project);
            Assert(!preview.Complete && preview.Files.Any(f => f.Path.EndsWith("file.txt"))
                && preview.Issues.Any(i => i.Code == "source.missing" && i.Path == missing && i.Message.Contains("Relink")), "Missing root was silently skipped or misclassified.");
        });
    }
}
