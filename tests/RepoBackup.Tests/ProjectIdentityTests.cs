using RepoBackup.Core.Discovery;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using RepoBackup.Core.Storage;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class ProjectIdentityTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        await tests.Run("Overlapping workspaces attach every existing group and review only unmatched folders", () =>
        {
            var directory = Path.Combine(root, "groups"); var a = DiscoveryFixtures.Folder(directory, "a"); var b = DiscoveryFixtures.Folder(directory, "b"); var c = DiscoveryFixtures.Folder(directory, "c");
            var catalog = DiscoveryFixtures.Catalog(directory);
            var first = catalog.RegisterCandidate("Frontend", [a], "Manual", approved: true);
            var second = catalog.RegisterCandidate("Backend", [b], "Manual", approved: true);
            catalog.SaveProject(second with { Dismissed = true, Enabled = false });
            var result = catalog.RegisterDiscovered("Workspace", [a, b, c], DiscoveryProviders.Antigravity, "workspace-id");
            Assert(result.Added == 1 && result.Projects.Count == 3 && catalog.Projects().Count == 3, "Workspace groups were combined or lost.");
            var updatedFirst = catalog.Projects().Single(p => p.Id == first.Id); var updatedSecond = catalog.Projects().Single(p => p.Id == second.Id);
            Assert(updatedFirst.Enabled && updatedFirst.Roots.Count == 1 && updatedSecond.Dismissed && !updatedSecond.Enabled && updatedSecond.Roots.Count == 1, "Existing grouping or review changed.");
            var added = catalog.Projects().Single(p => p.Id != first.Id && p.Id != second.Id);
            Assert(added.Roots.Single().Path == c && !added.Enabled && !added.Reviewed, "Unknown folders were silently enrolled.");
            Assert(catalog.Projects().All(p => p.DiscoverySources.Contains(new(DiscoveryProviders.Antigravity, "workspace-id"))), "Program metadata was not attached to every group.");
            Assert(catalog.RegisterDiscovered("Workspace", [a, b, c], DiscoveryProviders.Antigravity, "workspace-id").Added == 0, "Repeat added duplicates."); return Task.CompletedTask;
        });

        await tests.Run("New roots under an existing external ID require a separate review candidate", () =>
        {
            var directory = Path.Combine(root, "scope"); var first = DiscoveryFixtures.Folder(directory, "first"); var second = DiscoveryFixtures.Folder(directory, "second");
            var catalog = DiscoveryFixtures.Catalog(directory);
            var accepted = catalog.RegisterDiscovered("Accepted", [first], DiscoveryProviders.Codex, "same-id").Projects.Single();
            catalog.SaveProject(accepted with { Enabled = true, Reviewed = true });
            var result = catalog.RegisterDiscovered("Accepted", [first, second], DiscoveryProviders.Codex, "same-id");
            Assert(result.Added == 1 && catalog.Projects().Single(p => p.Id == accepted.Id).Roots.Count == 1, "External ID enlarged accepted backup scope.");
            Assert(catalog.Projects().Single(p => p.Id != accepted.Id).Roots.Single().Path == second && !catalog.Projects().Single(p => p.Id != accepted.Id).Enabled, "Added root did not need review."); return Task.CompletedTask;
        });

        await tests.Run("External IDs are provider scoped and folder identity survives concurrent registration", async () =>
        {
            var directory = Path.Combine(root, "concurrent"); var folder = DiscoveryFixtures.Folder(directory, "source");
            var catalog = DiscoveryFixtures.Catalog(directory); var other = new CatalogStore(new AppPaths(Path.Combine(directory, "catalog")));
            await Task.WhenAll(Enumerable.Range(0, 18).Select(i => Task.Run(() => (i % 2 == 0 ? catalog : other)
                .RegisterDiscovered("Concurrent", [folder], DiscoveryProviders.All[i % 3], "shared-id"))));
            var project = catalog.Projects().Single(); Assert(project.DiscoverySources.Count == 3, "Concurrent metadata was lost.");
            var separate = DiscoveryFixtures.Folder(directory, "separate");
            catalog.RegisterDiscovered("Separate", [separate], DiscoveryProviders.ClaudeCode, "shared-id");
            Assert(catalog.Projects().Count == 2, "External ID merged unrelated local folders.");
        });

        await tests.Run("Names, remote repositories, nested folders and worktree paths stay distinct", () =>
        {
            var directory = Path.Combine(root, "distinct"); var catalog = DiscoveryFixtures.Catalog(directory);
            var paths = new[] { "checkout", "checkout\\nested", "clone", "linked-worktree" }.Select(p => DiscoveryFixtures.Folder(directory, p)).ToList();
            foreach (var path in paths)
            {
                Directory.CreateDirectory(Path.Combine(path, ".git")); File.WriteAllText(Path.Combine(path, ".git", "config"), "[remote \"origin\"]\nurl=https://example.invalid/shared.git\n");
                catalog.RegisterDiscovered("Same name", [path], DiscoveryProviders.ClaudeCode, "shared-id");
            }
            Assert(catalog.Projects().Count == 4, "Different local folders were grouped by Git metadata or name."); return Task.CompletedTask;
        });

        await tests.Run("Junction aliases deduplicate across programs and survive an unavailable old target", async () =>
        {
            var directory = Path.Combine(root, "junction"); var source = DiscoveryFixtures.Folder(directory, "source"); var alias = Path.Combine(directory, "alias");
            (await new ProcessRunner().RunAsync("cmd.exe", ["/c", "mklink", "/J", alias, source])).EnsureSuccess("Create isolated junction");
            var catalog = DiscoveryFixtures.Catalog(directory); var original = catalog.RegisterDiscovered("Original", [source], DiscoveryProviders.Codex).Projects.Single();
            catalog.RegisterDiscovered("Alias", [alias], DiscoveryProviders.ClaudeCode);
            Assert(catalog.Projects().Count == 1 && catalog.Projects().Single().Roots.Single().Aliases.Contains(alias, StringComparer.OrdinalIgnoreCase), "Junction duplicated the project.");
            var moved = Path.Combine(directory, "moved");
            Assert(PathSafety.IsWithin(source, directory) && PathSafety.IsWithin(moved, directory), "Fixture move leaves its workspace."); Directory.Move(source, moved);
            catalog.RelinkRoot(original.Id, original.Roots.Single().Id, moved);
            catalog.RegisterDiscovered("Unavailable alias", [alias], DiscoveryProviders.Antigravity);
            var updated = catalog.Projects().Single(); Assert(updated.Id == original.Id && updated.Roots.Single().Path == moved && updated.DiscoverySources.Count == 3, "Unavailable alias recreated or undid relink.");
            await Throws<InvalidDataException>(() => { catalog.SaveProject(new ProjectEntry { Roots = [new(Guid.NewGuid().ToString("N"), moved)] }); return Task.CompletedTask; });
        });

        await tests.Run("Relinking and importing aliases cannot steal an existing folder", async () =>
        {
            var directory = Path.Combine(root, "conflicts"); var first = DiscoveryFixtures.Folder(directory, "first"); var second = DiscoveryFixtures.Folder(directory, "second");
            var catalog = DiscoveryFixtures.Catalog(directory); var a = catalog.RegisterCandidate("A", [first], "Manual", approved: true); catalog.RegisterCandidate("B", [second], "Manual", approved: true);
            var before = catalog.Export();
            await Throws<InvalidDataException>(() => { catalog.RelinkRoot(a.Id, a.Roots.Single().Id, second); return Task.CompletedTask; });
            var incoming = new ProjectEntry { Roots = [new(Guid.NewGuid().ToString("N"), Path.Combine(directory, "new")) { Aliases = [first] }] };
            await Throws<InvalidDataException>(() => { catalog.Import(Json.Write(new CatalogExport(2, [incoming], [], [], [], []))); return Task.CompletedTask; });
            Assert(catalog.Export() == before, "Rejected identity operation changed the catalog.");
        });

        await tests.Run("Saving stale UI settings preserves provider metadata added by a hook", () =>
        {
            var directory = Path.Combine(root, "stale"); var folder = DiscoveryFixtures.Folder(directory, "source"); var catalog = DiscoveryFixtures.Catalog(directory);
            var stale = catalog.RegisterCandidate("Source", [folder], "Manual", approved: true);
            catalog.RegisterDiscovered("Source", [folder], DiscoveryProviders.ClaudeCode);
            catalog.SaveProject(stale with { Enabled = false });
            Assert(catalog.Projects().Single().DiscoverySources.Any(s => s.ProviderId == DiscoveryProviders.ClaudeCode), "UI save discarded program identity."); return Task.CompletedTask;
        });
    }
}
