using RepoBackup.Core.Application;
using RepoBackup.Core.Backup;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using RepoBackup.Core.Recovery;
using RepoBackup.Core.Windows;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class WslLinkTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        var state = Path.Combine(root, "codex.json"); await File.WriteAllTextAsync(state, "{}");
        var app = new ApplicationServices(Path.Combine(root, "catalog"), codexStatePath: state);
        var project = app.Catalog.RegisterCandidate("WSL links", [source], "Fixture", approved: true);
        await tests.Run("Unavailable source paths do not require reading link metadata", () =>
        {
            var missing = Path.Combine(root, "unavailable", "old-link");
            Assert(WslLinks.SourcePath(missing) == PathSafety.Normalize(missing), "Unavailable source was not preserved lexically.");
            PathSafety.EnsureDestinationOutsideSources(Path.Combine(root, "new-restore"), [missing]);
            return Task.CompletedTask;
        });
        await tests.Run("WSL v1/v2 links preserve raw targets and timestamps without WSL or target access", async () =>
        {
            var targets = new[] { "../missing", "/opt/unavailable/thing", "/mnt/c/外部/é.txt", "cycle", "../../outside" };
            foreach (var version in new uint[] { 1, 2 })
            foreach (var (target, index) in targets.Select((value, index) => (value, index)))
            {
                var path = Path.Combine(source, $"v{version}-{index}"); WslLinkFixtures.Create(path, target, version);
                var data = WslLinks.Read(path)!; Assert(data is not null, "Did not identify WSL link.");
                Assert(WslLinks.Read(@"\\?\" + path)?.Fingerprint == data!.Fingerprint, "Extended Windows path changed link capture.");
                var restored = Path.Combine(root, "native-restore", $"v{version}-{index}");
                WslLinks.Create(restored, data!, root);
                var copy = WslLinks.Read(restored)!;
                Assert(copy.ReparseData.SequenceEqual(data!.ReparseData) && copy.LegacyTarget.SequenceEqual(data.LegacyTarget) && copy.Fingerprint == data.Fingerprint, "Link metadata changed.");
            }
            var preview = await app.Planner.PreviewAsync(project);
            Assert(preview.Complete && preview.WslLinks.Count == 10 && preview.Files.Count == 10, string.Join("; ", preview.Warnings));
        });
        await tests.Run("Selecting a WSL leaf captures the link, and exclusions still apply", async () =>
        {
            var selection = new SavedSelection { ProjectId = project.Id, Name = "one link", Paths = [Path.Combine(source, "v2-0")] };
            var preview = await app.Planner.PreviewAsync(project, selection);
            Assert(preview.Complete && preview.WslLinks.Single().RelativePath == ".", "Leaf selection followed the link.");
            var folder = new RepoBackup.Desktop.Views.FolderNode(source, []); folder.Load();
            var leaf = folder.Children.Single(c => c.Path == selection.Paths[0]);
            Assert(leaf.Children.Count == 0, "Selection UI expands link targets.");
            leaf.Included = true;
            Assert(folder.SelectedPaths().Contains(selection.Paths[0]), "Selection UI omitted selected WSL link.");
            var excluded = await app.Planner.PreviewAsync(project with { Exclusions = new() { RelativePatterns = ["v1-*"] } });
            Assert(excluded.Complete && excluded.WslLinks.Count == 5 && excluded.Excluded.Count == 5, "Exclusions did not apply to WSL links.");
        });
        await tests.Run("Retargeting with unchanged timestamps is detected for live and VSS metadata checks", async () =>
        {
            var before = await app.Planner.PreviewAsync(project);
            var path = Path.Combine(source, "v2-0"); var original = WslLinks.Read(path)!;
            File.Delete(path); WslLinkFixtures.Create(path, "../changed");
            var changed = WslLinks.Read(path)! with { CreationTime = original.CreationTime, AccessTime = original.AccessTime, WriteTime = original.WriteTime };
            File.Delete(path); WslLinks.Create(path, changed, root);
            var after = await app.Planner.PreviewAsync(project);
            Assert(SelectionPlanner.Changed(before, after) && SelectionPlanner.WslLinksChanged(before, after), "Retargeting was missed.");
        });
        await tests.Run("Unknown WSL formats remain coverage warnings", async () =>
        {
            var path = Path.Combine(source, "future-link"); WslLinkFixtures.Create(path, "missing", 3);
            try
            {
                var preview = await app.Planner.PreviewAsync(project);
                Assert(!preview.Complete && preview.Warnings.Any(w => w.Contains("Unsupported WSL link version")) && !preview.Files.Any(f => f.Path == path), "Unknown link was silently accepted.");
            }
            finally { File.Delete(path); }
        });
        await tests.Run("Unreadable WSL links report incomplete coverage and recover after unlocking", async () =>
        {
            using (WslLinkFixtures.Lock(Path.Combine(source, "v2-0")))
            {
                var preview = await app.Planner.PreviewAsync(project);
                Assert(!preview.Complete && preview.Warnings.Any(w => w.Contains("v2-0")) && !preview.WslLinks.Any(l => l.RelativePath == "v2-0"), "Locked WSL link was reported as captured.");
            }
            Assert((await app.Planner.PreviewAsync(project)).Complete, "Unlock did not restore coverage.");
        });
        await tests.Run("Manifest validation rejects corrupt buffers, unsafe paths, duplicates and downgraded versions", async () =>
        {
            var preview = await app.Planner.PreviewAsync(project);
            var manifest = new RecoveryManifest { SchemaVersion = 2, JobId = "test", SeriesId = "test", Project = project, Sources = preview.Sources, WslLinks = preview.WslLinks };
            WslLinkMetadata.Validate(manifest);
            var link = manifest.WslLinks[0];
            foreach (var path in new[] { "../escape", "/absolute", "a/../escape", "a\\escape", "a:stream", "trailing.", "a//b" })
                await Throws<InvalidDataException>(() => Task.Run(() => WslLinkMetadata.Validate(manifest with { WslLinks = [link with { RelativePath = path }] })));
            await Throws<InvalidDataException>(() => Task.Run(() => WslLinkMetadata.Validate(manifest with { SchemaVersion = 1 })));
            await Throws<InvalidDataException>(() => Task.Run(() => WslLinkMetadata.Validate(manifest with { WslLinks = [link, link] })));
            await Throws<InvalidDataException>(() => Task.Run(() => WslLinkMetadata.Validate(manifest with { WslLinks = [link with { Data = link.Data with { ReparseData = [1, 2, 3] } }] })));
            await Throws<InvalidDataException>(() => Task.Run(() => SnapshotContents.Merge(manifest, [new(WslLinkMetadata.SnapshotPath(manifest.Sources[0], link), "file", 0)])));
            await Throws<InvalidDataException>(() => Task.Run(() => SnapshotContents.Merge(manifest, [new(WslLinkMetadata.SnapshotPath(manifest.Sources[0], link) + "/child", "file", 0)])));
            await Throws<InvalidDataException>(() => Task.Run(() => SnapshotContents.Merge(manifest, [new(manifest.Sources[0].SnapshotPath, "symlink", 0)])));
            Assert(SnapshotContents.Merge(manifest, []).Count(n => n.Type == "symlink") == 10, "Logical link-only tree missing entries.");
            WslLinkMetadata.Validate(manifest with { SchemaVersion = 1, WslLinks = [] });
        });
        await tests.Run("WSL restoration refuses collisions and reparse ancestors", async () =>
        {
            var data = WslLinks.Read(Path.Combine(source, "v2-0"))!;
            var collision = Path.Combine(root, "collision"); await File.WriteAllTextAsync(collision, "keep");
            await Throws<IOException>(() => Task.Run(() => WslLinks.Create(collision, data, root)));
            Assert(await File.ReadAllTextAsync(collision) == "keep", "Existing data overwritten.");
            var outside = Path.Combine(root, "external"); Directory.CreateDirectory(outside);
            var junction = Path.Combine(root, "junction");
            (await new ProcessRunner().RunAsync("cmd.exe", ["/c", "mklink", "/J", junction, outside])).EnsureSuccess("Create isolated junction");
            await Throws<InvalidDataException>(() => Task.Run(() => WslLinks.Create(Path.Combine(junction, "escape"), data, root)));
            Assert(!File.Exists(Path.Combine(outside, "escape")), "Restore followed junction.");
        });
    }
}
