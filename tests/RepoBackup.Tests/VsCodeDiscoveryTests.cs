using RepoBackup.Core.Discovery;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using RepoBackup.Core.Windows;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class VsCodeDiscoveryTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        await tests.Run("VS Code discovers ordinary folders and JSONC workspace groups across stable, Insiders and profiles", async () =>
        {
            var directory = Path.Combine(root, "locations"); var first = DiscoveryFixtures.Folder(directory, "first 雪");
            var second = DiscoveryFixtures.Folder(directory, "second"); var profileFolder = DiscoveryFixtures.Folder(directory, "profile");
            var insidersFolder = DiscoveryFixtures.Folder(directory, "insiders"); var stable = Path.Combine(directory, "Code"); var insiders = Path.Combine(directory, "Code - Insiders");
            var workspace = Path.Combine(directory, "Group.code-workspace");
            await File.WriteAllTextAsync(workspace, "{ // JSONC\n\"folders\":[{\"path\":\"first 雪\"},{\"uri\":" + Json.Write(DiscoveryFixtures.FileUri(second)) + "},],}");
            var db = ExtensionDiscoveryFixtures.Registry(Path.Combine(stable, "User", "globalStorage", "state.vscdb"), new Dictionary<string, string>
            {
                ["history.recentlyOpenedPathsList"] = Json.Write(new { entries = new object[]
                { new { workspace = new { configPath = DiscoveryFixtures.FileUri(workspace) } }, new { folderUri = DiscoveryFixtures.FileUri(first) },
                  new { fileUri = DiscoveryFixtures.FileUri(Path.Combine(directory, "individual.txt")) }, new { folderUri = "vscode-remote://ssh-remote+host/project" } } }),
                ["GitHub.copilot-chat"] = "fixture-private-data"
            });
            await ExtensionDiscoveryFixtures.Workspace(stable, "group", workspace, workspace: true);
            ExtensionDiscoveryFixtures.Registry(Path.Combine(stable, "User", "profiles", "profile-id", "globalStorage", "state.vscdb"), new Dictionary<string, string>
            { ["history.recentlyOpenedPathsList"] = ExtensionDiscoveryFixtures.RecentFolders(profileFolder) });
            await ExtensionDiscoveryFixtures.Workspace(insiders, "insiders-workspace", insidersFolder);
            var hash = await Fixture.HashAsync(db); var catalog = DiscoveryFixtures.Catalog(directory);
            var result = await new ProjectDiscoveryService(catalog, [new VsCodeDiscovery([stable, insiders])]).RefreshAsync();
            Assert(result is { Added: 3, Projects: 3, Roots: 4 } && result.Warnings.Count == 0, Json.Write(result));
            Assert(catalog.Projects().Single(p => p.Roots.Count == 2).Roots.Select(r => r.Path).ToHashSet().SetEquals([first, second]), "Workspace grouping or relative/URI folder parsing changed.");
            Assert(catalog.Projects().All(p => p.DiscoverySources.All(s => s.ProviderId == DiscoveryProviders.VsCode)), "Global extension state was attributed to every project.");
            Assert(await Fixture.HashAsync(db) == hash && !catalog.Export().Contains("fixture-private-data"), "VS Code registry modified or private extension state persisted.");
            Assert(AppPaths.DefaultVsCodeUserDataDirectories.Select(Path.GetFileName).SequenceEqual(["Code", "Code - Insiders"]), "Default locations omit stable or Insiders.");
        });

        await tests.Run("Workspace-owned extension keys share family labels and preserve approved project identity and history", async () =>
        {
            var directory = Path.Combine(root, "associations"); var folder = DiscoveryFixtures.Folder(directory, "source"); var userData = Path.Combine(directory, "vscode");
            var storage = await ExtensionDiscoveryFixtures.Workspace(userData, "workspace", folder, new Dictionary<string, string>
            { ["OpenAI.ChatGPT"] = "fixture-private-codex", ["Anthropic.claude-code"] = "fixture-private-claude", ["GitHub.copilot-chat"] = "fixture-private-copilot", ["GitHub.copilot"] = "fixture-private-inline" });
            var catalog = DiscoveryFixtures.Catalog(directory); var original = catalog.RegisterCandidate("My chosen name", [folder], "Manual", approved: true);
            var selection = new SavedSelection { ProjectId = original.Id, Name = "Saved selection", Paths = ["assets"] }; catalog.SaveSelection(selection);
            var job = new JobRecord { ProjectId = original.Id, ProjectName = original.Name, DestinationId = "fixture", SeriesId = original.Id, Backup = Outcome.Successful }; catalog.SaveJob(job);
            var state = await DiscoveryFixtures.CodexState(directory, folder); var claude = await DiscoveryFixtures.ClaudeState(directory, folder);
            var service = new ProjectDiscoveryService(catalog, [new CodexDiscovery(state), new ClaudeCodeDiscovery(claude), new VsCodeDiscovery(userData)]);
            var db = Path.Combine(storage, "state.vscdb"); var hash = await Fixture.HashAsync(db);
            var first = await service.RefreshAsync(); var export = catalog.Export(); var again = await service.RefreshAsync();
            var project = catalog.Projects().Single();
            Assert(first.Added == 0 && again.Added == 0 && project.Id == original.Id && project.Name == original.Name && project.Enabled && project.Reviewed, "Discovery changed approval, identity or name.");
            Assert(project.DiscoverySources.Select(s => s.ProviderId).ToHashSet().SetEquals(["manual", DiscoveryProviders.Codex, DiscoveryProviders.ClaudeCode, DiscoveryProviders.VsCode, DiscoveryProviders.Copilot]), "Source families missing or split by client.");
            Assert(catalog.Selections().Single().Id == selection.Id && catalog.History().Single().Id == job.Id && catalog.Export() == export, "Repeated discovery changed saved data.");
            Assert(await Fixture.HashAsync(db) == hash && !export.Contains("fixture-private"), "Extension state value leaked or database changed.");
            catalog.SaveProject(project with { Dismissed = true, Enabled = false });
            await service.RefreshAsync(); Assert(catalog.Projects().Single() is { Dismissed: true, Enabled: false }, "Extension discovery reset dismissal.");
            Assert(DiscoveryProviders.DisplayName(DiscoveryProviders.VsCode) == "VS Code" && DiscoveryProviders.DisplayName(DiscoveryProviders.Copilot) == "GitHub Copilot", "Display labels are not searchable program names.");
        });

        await tests.Run("Installed extensions and generic chat history do not identify an assistant for an ordinary workspace", async () =>
        {
            var directory = Path.Combine(root, "unattributed"); var folder = DiscoveryFixtures.Folder(directory, "source"); var userData = Path.Combine(directory, "portable-data");
            ExtensionDiscoveryFixtures.Registry(Path.Combine(userData, "User", "globalStorage", "state.vscdb"), new Dictionary<string, string>
            { ["openai.chatgpt"] = "installed", ["Anthropic.claude-code"] = "installed", ["GitHub.copilot-chat"] = "installed", ["history.recentlyOpenedPathsList"] = ExtensionDiscoveryFixtures.RecentFolders(folder) });
            await ExtensionDiscoveryFixtures.Workspace(userData, "plain", folder, new Dictionary<string, string>
            { ["chat.ChatSessionStore.index"] = "fixture-private-generic-history", ["unrelated.extension"] = "fixture-private-data" });
            var generic = await new VsCodeDiscovery(userData).ReadAsync();
            Assert(generic.Projects.Single().AssociatedProviders.Count == 0 && generic.Warnings.Count == 0, "Generic or installed state attributed an assistant.");
            foreach (var source in new[] { DiscoveryProviders.Codex, DiscoveryProviders.ClaudeCode, DiscoveryProviders.Copilot })
                Assert((await new VsCodeDiscovery(userData, source).ReadAsync()).Projects.Count == 0, "Assistant source discovered ordinary workspaces.");
            Assert((await new VsCodeDiscovery(Path.Combine(userData, "User")).ReadAsync()).Projects.Single().Roots.Single() == folder, "User-directory override changed location resolution.");
        });

        await tests.Run("VS Code refresh sees new metadata and assistant filters exclude unrelated sources", async () =>
        {
            var directory = Path.Combine(root, "refresh"); var codex = DiscoveryFixtures.Folder(directory, "codex"); var claude = DiscoveryFixtures.Folder(directory, "claude");
            var copilot = DiscoveryFixtures.Folder(directory, "copilot"); var userData = Path.Combine(directory, "vscode"); var provider = new VsCodeDiscovery(userData);
            await ExtensionDiscoveryFixtures.Workspace(userData, "codex", codex, new Dictionary<string, string> { ["openai.chatgpt"] = "private" });
            Assert((await provider.ReadAsync()).Projects.Count == 1, "Initial workspace missing.");
            await ExtensionDiscoveryFixtures.Workspace(userData, "claude", claude, new Dictionary<string, string> { ["anthropic.claude-code"] = "private" });
            await ExtensionDiscoveryFixtures.Workspace(userData, "copilot", copilot, new Dictionary<string, string> { ["github.copilot"] = "private" });
            Assert((await provider.ReadAsync()).Projects.Count == 3, "VS Code state was cached across refreshes.");
            foreach (var (source, path) in new[] { (DiscoveryProviders.Codex, codex), (DiscoveryProviders.ClaudeCode, claude), (DiscoveryProviders.Copilot, copilot) })
            {
                var catalog = DiscoveryFixtures.Catalog(Path.Combine(directory, source));
                var result = await new ProjectDiscoveryService(catalog, [new VsCodeDiscovery(userData, source)]).RefreshAsync();
                Assert(result.Added == 1 && catalog.Projects().Single().Roots.Single().Path == path && catalog.Projects().Single().DiscoverySources.Single().ProviderId == source, "Source filter leaked another assistant or a VS Code label.");
            }
        });

        await tests.Run("Corrupt and locked VS Code registries preserve folder discovery and other providers", async () =>
        {
            var directory = Path.Combine(root, "failures"); var folder = DiscoveryFixtures.Folder(directory, "source"); var userData = Path.Combine(directory, "vscode");
            var db = ExtensionDiscoveryFixtures.Registry(Path.Combine(userData, "User", "globalStorage", "state.vscdb"), new Dictionary<string, string>());
            await File.WriteAllTextAsync(db, "fixture-private-invalid-db");
            var storage = await ExtensionDiscoveryFixtures.Workspace(userData, "known", folder, new Dictionary<string, string> { ["GitHub.copilot-chat"] = "private" });
            var config = await DiscoveryFixtures.ClaudeState(directory, folder); var catalog = DiscoveryFixtures.Catalog(directory);
            using (var locked = new FileStream(Path.Combine(storage, "state.vscdb"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var result = await new ProjectDiscoveryService(catalog, [new VsCodeDiscovery(userData), new ClaudeCodeDiscovery(config)]).RefreshAsync();
                Assert(result.Added == 1 && result.Warnings.Count == 2 && catalog.Projects().Single().DiscoverySources.Select(s => s.ProviderId).ToHashSet().SetEquals([DiscoveryProviders.VsCode, DiscoveryProviders.ClaudeCode]), Json.Write(result));
                Assert(!string.Join(" ", result.Warnings).Contains("fixture-private"), "Warnings exposed source contents.");
            }
            var missing = await new VsCodeDiscovery(Path.Combine(directory, "absent")).ReadAsync();
            Assert(missing.Projects.Count == 0 && missing.Warnings.Count == 0, "Missing editor warned or created a candidate.");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Throws<OperationCanceledException>(async () => await new VsCodeDiscovery(userData).ReadAsync(cancelled.Token));
        });

        await tests.Run("New discovery sources do not acquire native hooks", async () =>
        {
            var directory = Path.Combine(root, "hooks"); var folder = DiscoveryFixtures.Folder(directory, "source");
            var paths = new AppPaths(Path.Combine(directory, "catalog")); var catalog = DiscoveryFixtures.Catalog(directory);
            foreach (var source in new[] { DiscoveryProviders.VsCode, DiscoveryProviders.Copilot })
            {
                await Throws<ArgumentException>(() => { HookCommandBuilder.Build("C:\\Fixture\\Runner.exe", source, paths.DataDirectory); return Task.CompletedTask; });
                await Throws<ArgumentException>(() => { DiscoveryHookInstaller.Install(source, catalog, paths.DataDirectory); return Task.CompletedTask; });
                Assert(!await HookRegistration.RegisterAsync(source, paths, new StringReader(Json.Write(new { cwd = folder }))), "Discovery-only source accepted a native hook.");
            }
            Assert(catalog.Projects().Count == 0 && DiscoveryProviders.All.Length == 5 && DiscoveryProviders.HookCapable.Length == 3, "Provider lists or rejected hooks changed the catalog.");
        });
    }
}
