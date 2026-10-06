using System.Text.Json;
using RepoBackup.Core.Application;
using RepoBackup.Core.Discovery;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class DiscoveryTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        await tests.Run("Three program registries produce one project with stable approval and history", async () =>
        {
            var directory = Path.Combine(root, "all-programs"); var folder = DiscoveryFixtures.Folder(directory, "Shared project");
            var catalog = DiscoveryFixtures.Catalog(directory);
            var codex = await DiscoveryFixtures.CodexState(directory, folder);
            var claude = await DiscoveryFixtures.ClaudeState(directory, folder.ToUpperInvariant().Replace('\\', '/') + "/");
            var antigravity = DiscoveryFixtures.AntigravityDatabase(directory, new Dictionary<string, string>
            {
                ["history.recentlyOpenedPathsList"] = Json.Write(new { entries = new[] { new { folderUri = DiscoveryFixtures.FileUri(folder).Replace("C:/", "c%3A/") } } }),
                ["antigravityUnifiedStateSync.sidebarWorkspaces"] = DiscoveryFixtures.Sidebar(DiscoveryFixtures.FileUri(folder)),
                ["private-auth-state"] = "fixture-private-data"
            });
            var discovery = new ProjectDiscoveryService(catalog, [new CodexDiscovery(codex), new ClaudeCodeDiscovery(claude), new AntigravityDiscovery(antigravity)]);
            var result = await discovery.RefreshAsync(); Assert(result.Added == 1 && result.Projects == 1 && result.Warnings.Count == 0, Json.Write(result));
            var project = catalog.Projects().Single(); Assert(project.DiscoverySources.Select(s => s.ProviderId).Distinct().Count() == 3 && !project.Reviewed && !project.Enabled, "Incorrect origins or automatic approval.");
            catalog.SaveProject(project with { Reviewed = true, Enabled = true });
            var selection = new SavedSelection { ProjectId = project.Id, Name = "Selection", Paths = [folder] }; catalog.SaveSelection(selection);
            var job = new JobRecord { ProjectId = project.Id, ProjectName = project.Name, SeriesId = "project-" + project.Id, DestinationId = "fixture", Backup = Outcome.Successful, Verification = Outcome.Successful, FinishedAt = DateTimeOffset.UtcNow }; catalog.SaveJob(job);
            await discovery.RefreshAsync(); var updated = catalog.Projects().Single();
            Assert(updated.Id == project.Id && updated.Roots.Single().Id == project.Roots.Single().Id && updated.Enabled && updated.Reviewed, "Identity or review changed.");
            Assert(catalog.Selections().Single().ProjectId == project.Id && catalog.History().Single().SeriesId == job.SeriesId, "History or selection changed.");
            Assert(!catalog.Export().Contains("fixture-private-data"), "Private provider data entered the catalog.");
        });

        await tests.Run("Claude session indexes discover path metadata without decoding directory slugs", async () =>
        {
            var directory = Path.Combine(root, "claude-index"); var first = DiscoveryFixtures.Folder(directory, "Hyphen-name 雪"); var second = DiscoveryFixtures.Folder(directory, "second");
            var config = await DiscoveryFixtures.ClaudeState(directory, first);
            var indexes = DiscoveryFixtures.Folder(directory, "projects\\non-reversible-slug");
            await File.WriteAllTextAsync(Path.Combine(indexes, "sessions-index.json"), Json.Write(new { originalPath = first, entries = new[] { new { projectPath = second, firstPrompt = "fixture-private-data", transcriptPath = "unused" } } }));
            var catalog = DiscoveryFixtures.Catalog(directory); var service = new ProjectDiscoveryService(catalog, [new ClaudeCodeDiscovery(config)]);
            var result = await service.RefreshAsync(); Assert(result.Added == 2 && result.Warnings.Count == 0, Json.Write(result));
            Assert(!catalog.Export().Contains("fixture-private-data") && !catalog.Export().Contains("non-reversible-slug"), "Session contents or encoded directory copied.");
        });

        await tests.Run("Claude configuration directory override stays isolated from real configuration", async () =>
        {
            var directory = DiscoveryFixtures.Folder(root, "claude-override"); var folder = DiscoveryFixtures.Folder(directory, "source");
            var config = await DiscoveryFixtures.ClaudeState(directory, folder); File.Move(config, Path.Combine(directory, ".claude.json"));
            var old = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            try
            {
                Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", directory);
                var result = await new ClaudeCodeDiscovery().ReadAsync(); Assert(result.Projects.Single().Roots.Single() == folder && result.Warnings.Count == 0, "Override was ignored.");
            }
            finally { Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", old); }
        });

        await tests.Run("Antigravity JSONC multi-root workspaces resolve relative paths and skip remote/file history", async () =>
        {
            var directory = Path.Combine(root, "workspaces"); var first = DiscoveryFixtures.Folder(directory, "frontend"); var second = DiscoveryFixtures.Folder(directory, "backend");
            var workspace = Path.Combine(directory, "Product.code-workspace");
            var json = "{\n// Workspace folders\n\"folders\":[{\"path\":\"frontend\"},{\"uri\":" + JsonSerializer.Serialize(DiscoveryFixtures.FileUri(second)) + "},{\"uri\":\"vscode-remote://ssh-remote+host/project\"},],}";
            await File.WriteAllTextAsync(workspace, json);
            var userData = DiscoveryFixtures.AntigravityDatabase(directory, new Dictionary<string, string>
            {
                ["history.recentlyOpenedPathsList"] = Json.Write(new { entries = new object[]
                { new { workspace = new { configPath = DiscoveryFixtures.FileUri(workspace) } }, new { fileUri = DiscoveryFixtures.FileUri(Path.Combine(directory, "individual.txt")) }, new { folderUri = "vscode-remote://ssh-remote+host/project" } } }),
                ["antigravityUnifiedStateSync.sidebarWorkspaces"] = DiscoveryFixtures.Sidebar(DiscoveryFixtures.FileUri(first))
            });
            var metadata = DiscoveryFixtures.Folder(userData, "User\\workspaceStorage\\known-id");
            await File.WriteAllTextAsync(Path.Combine(metadata, "workspace.json"), Json.Write(new { workspace = DiscoveryFixtures.FileUri(workspace) }));
            var database = Path.Combine(userData, "User", "globalStorage", "state.vscdb"); var hash = await Fixture.HashAsync(database);
            var catalog = DiscoveryFixtures.Catalog(directory); var result = await new ProjectDiscoveryService(catalog, [new AntigravityDiscovery(userData)]).RefreshAsync();
            Assert(result.Projects == 1 && result.Roots == 2 && result.Warnings.Count == 0, Json.Write(result));
            Assert(catalog.Projects().Single().Roots.Select(r => r.Path).ToHashSet().SetEquals([first, second]), "Workspace folders missing.");
            Assert(await Fixture.HashAsync(database) == hash, "Antigravity database was changed.");
        });

        await tests.Run("Workspace file URIs decode drive colons, Unicode and UNC shares", () =>
        {
            Assert(WorkspaceFileReader.LocalPath("file:///c%3A/Projects/My%20Project/%E9%9B%AA") == "c:\\Projects\\My Project\\雪", "Encoded file URI was not decoded.");
            Assert(WorkspaceFileReader.LocalPath("file://server/share/project") == "\\\\server\\share\\project", "UNC URI was not decoded.");
            Assert(WorkspaceFileReader.LocalPath("file://localhost/C:/Projects/100%2520percent") == "C:\\Projects\\100%20percent", "File URI was decoded more than once.");
            Assert(WorkspaceFileReader.LocalPath("file:///home/project") is null, "Non-Windows path interpreted locally.");
            Assert(WorkspaceFileReader.LocalPath("vscode-remote://ssh-remote+host/project") is null, "Remote URI interpreted locally."); return Task.CompletedTask;
        });

        await tests.Run("Malformed provider state preserves its catalog and does not stop another provider", async () =>
        {
            var directory = Path.Combine(root, "malformed"); var first = DiscoveryFixtures.Folder(directory, "first"); var second = DiscoveryFixtures.Folder(directory, "second");
            var catalog = DiscoveryFixtures.Catalog(directory); var existing = catalog.RegisterCandidate("Existing", [first], "Manual", approved: true);
            var before = Json.Write(existing); var codex = await DiscoveryFixtures.CodexState(directory, first);
            await File.WriteAllTextAsync(codex, "{\"local-projects\":{\"good\":{\"rootPaths\":[" + JsonSerializer.Serialize(second) + "]},\"bad\":{\"rootPaths\":42}}}");
            var claude = await DiscoveryFixtures.ClaudeState(directory, second);
            var userData = DiscoveryFixtures.AntigravityDatabase(directory, new Dictionary<string, string> { ["antigravityUnifiedStateSync.sidebarWorkspaces"] = "invalid-secret-fixture" });
            var result = await new ProjectDiscoveryService(catalog, [new CodexDiscovery(codex), new AntigravityDiscovery(userData), new ClaudeCodeDiscovery(claude)]).RefreshAsync();
            Assert(result.Added == 1 && result.Warnings.Count == 2 && catalog.Projects().Count == 2, Json.Write(result));
            Assert(Json.Write(catalog.Projects().Single(p => p.Id == existing.Id)) == before, "Corrupt provider changed an existing project.");
            Assert(!string.Join(" ", result.Warnings).Contains("invalid-secret-fixture"), "Warnings contain source content.");
        });

        await tests.Run("Sidebar decoder rejects truncated data and accepts forward-compatible fields", async () =>
        {
            var uri = "file:///C:/Projects/Shared"; Assert(AntigravitySidebarReader.ReadWorkspaceUris(DiscoveryFixtures.Sidebar(uri)).Single() == uri, "Workspace URI lost.");
            await Throws<InvalidDataException>(() => { AntigravitySidebarReader.ReadWorkspaceUris(Convert.ToBase64String([10, 127, 10])); return Task.CompletedTask; });
        });

        await tests.Run("Inaccessible provider input warns without changing existing projects or blocking other readers", async () =>
        {
            var directory = Path.Combine(root, "inaccessible"); var folder = DiscoveryFixtures.Folder(directory, "source");
            var config = await DiscoveryFixtures.ClaudeState(directory, folder); var codex = await DiscoveryFixtures.CodexState(directory, folder);
            var catalog = DiscoveryFixtures.Catalog(directory);
            using var locked = new FileStream(config, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var result = await new ProjectDiscoveryService(catalog, [new ClaudeCodeDiscovery(config), new CodexDiscovery(codex)]).RefreshAsync();
            Assert(result is { Added: 1, Projects: 1 } && result.Warnings.Count == 1 && result.Warnings.Single().StartsWith("Claude Code"), Json.Write(result));
            Assert(catalog.Projects().Single().DiscoverySources.Single().ProviderId == DiscoveryProviders.Codex, "Unreadable provider registered paths.");
        });

        await tests.Run("Corrupt Antigravity database still permits workspace-storage metadata discovery", async () =>
        {
            var directory = Path.Combine(root, "bad-database"); var folder = DiscoveryFixtures.Folder(directory, "source");
            var userData = DiscoveryFixtures.AntigravityDatabase(directory, new Dictionary<string, string>());
            var database = Path.Combine(userData, "User", "globalStorage", "state.vscdb"); await File.WriteAllTextAsync(database, "private-invalid-database-fixture");
            var metadata = DiscoveryFixtures.Folder(userData, "User\\workspaceStorage\\workspace-id");
            await File.WriteAllTextAsync(Path.Combine(metadata, "workspace.json"), Json.Write(new { folder = DiscoveryFixtures.FileUri(folder) }));
            var hash = await Fixture.HashAsync(database); var catalog = DiscoveryFixtures.Catalog(directory);
            var result = await new ProjectDiscoveryService(catalog, [new AntigravityDiscovery(userData)]).RefreshAsync();
            Assert(result is { Added: 1, Projects: 1 } && result.Warnings.Count == 1 && result.Warnings.Single().StartsWith("Antigravity database"), Json.Write(result));
            Assert(await Fixture.HashAsync(database) == hash && !catalog.Export().Contains("private-invalid") && !result.Warnings.Single().Contains("private-invalid"), "Bad registry changed or leaked source data.");
        });

        await tests.Run("Stale Antigravity workspace files on an available volume are skipped without changing existing projects", async () =>
        {
            var directory = Path.Combine(root, "stale-workspaces"); var oldRoot = DiscoveryFixtures.Folder(directory, "old-project");
            var folder = DiscoveryFixtures.Folder(directory, "current-project"); var missing = Path.Combine(directory, "removed-directory", "Old.code-workspace");
            var userData = DiscoveryFixtures.AntigravityDatabase(directory, new Dictionary<string, string>
            {
                ["history.recentlyOpenedPathsList"] = Json.Write(new { entries = new object[]
                { new { workspace = new { configPath = DiscoveryFixtures.FileUri(missing) } }, new { folderUri = DiscoveryFixtures.FileUri(folder) } } })
            });
            var metadata = DiscoveryFixtures.Folder(userData, "User\\workspaceStorage\\stale");
            var metadataFile = Path.Combine(metadata, "workspace.json");
            await File.WriteAllTextAsync(metadataFile, Json.Write(new { workspace = DiscoveryFixtures.FileUri(Path.Combine(directory, "Old.workspace.json")) }));
            var catalog = DiscoveryFixtures.Catalog(directory); var existing = catalog.RegisterCandidate("Existing", [oldRoot], "Manual", approved: true);
            var before = Json.Write(existing); var database = Path.Combine(userData, "User", "globalStorage", "state.vscdb");
            var databaseHash = await Fixture.HashAsync(database); var metadataHash = await Fixture.HashAsync(metadataFile);
            var result = await new ProjectDiscoveryService(catalog, [new AntigravityDiscovery(userData)]).RefreshAsync();
            Assert(result.Added == 1 && result.Projects == 2 && result.Warnings.Count == 0, Json.Write(result));
            Assert(Json.Write(catalog.Projects().Single(p => p.Id == existing.Id)) == before, "Stale reference changed an existing project.");
            Assert(await Fixture.HashAsync(database) == databaseHash && await Fixture.HashAsync(metadataFile) == metadataHash, "Discovery modified Antigravity metadata.");
        });

        await tests.Run("Antigravity referenced workspaces on an unavailable volume still warn", async () =>
        {
            var directory = Path.Combine(root, "offline-workspace"); var folder = DiscoveryFixtures.Folder(directory, "current-project");
            var volume = Enumerable.Range('D', 23).Select(letter => ((char)letter) + ":\\").First(path => !Directory.Exists(path));
            var userData = DiscoveryFixtures.AntigravityDatabase(directory, new Dictionary<string, string>
            {
                ["history.recentlyOpenedPathsList"] = Json.Write(new { entries = new object[]
                { new { workspace = new { configPath = DiscoveryFixtures.FileUri(Path.Combine(volume, "Unavailable.code-workspace")) } }, new { folderUri = DiscoveryFixtures.FileUri(folder) } } })
            });
            var result = await new ProjectDiscoveryService(DiscoveryFixtures.Catalog(directory), [new AntigravityDiscovery(userData)]).RefreshAsync();
            Assert(result.Added == 1 && result.Warnings.Count == 1 && result.Warnings[0].StartsWith("Antigravity referenced workspace"), Json.Write(result));
        });

        await tests.Run("Locked and malformed Antigravity workspace files warn while valid folders are discovered", async () =>
        {
            var directory = Path.Combine(root, "unreadable-workspaces"); var folder = DiscoveryFixtures.Folder(directory, "current-project");
            var lockedFile = Path.Combine(directory, "Locked.code-workspace"); var invalidFile = Path.Combine(directory, "Invalid.code-workspace");
            await File.WriteAllTextAsync(lockedFile, "{\"folders\":[]}"); await File.WriteAllTextAsync(invalidFile, "private-invalid-workspace-fixture");
            var userData = DiscoveryFixtures.AntigravityDatabase(directory, new Dictionary<string, string>
            {
                ["history.recentlyOpenedPathsList"] = Json.Write(new { entries = new object[]
                { new { workspace = new { configPath = DiscoveryFixtures.FileUri(lockedFile) } }, new { workspace = new { configPath = DiscoveryFixtures.FileUri(invalidFile) } }, new { folderUri = DiscoveryFixtures.FileUri(folder) } } })
            });
            using var locked = new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var result = await new ProjectDiscoveryService(DiscoveryFixtures.Catalog(directory), [new AntigravityDiscovery(userData)]).RefreshAsync();
            Assert(result.Added == 1 && result.Warnings.Count == 2 && result.Warnings.All(w => w.StartsWith("Antigravity referenced workspace") && !w.Contains("private-invalid")), Json.Write(result));
        });

        await tests.Run("Missing default installations produce no warnings and cancellation propagates", async () =>
        {
            var directory = DiscoveryFixtures.Folder(root, "missing"); var oldCodex = Environment.GetEnvironmentVariable("CODEX_HOME"); var oldClaude = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            try
            {
                Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(directory, "codex")); Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Path.Combine(directory, "claude"));
                var catalog = DiscoveryFixtures.Catalog(directory); var service = new ProjectDiscoveryService(catalog, [new CodexDiscovery(), new ClaudeCodeDiscovery(), new AntigravityDiscovery(Path.Combine(directory, "antigravity"))]);
                Assert((await service.RefreshAsync()).Warnings.Count == 0 && catalog.Projects().Count == 0, "Missing program warned or created a candidate.");
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                await Throws<OperationCanceledException>(async () => await service.RefreshAsync(token: cancelled.Token));
            }
            finally { Environment.SetEnvironmentVariable("CODEX_HOME", oldCodex); Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", oldClaude); }
        });

        await tests.Run("Application service provider injection isolates scheduled and backup discovery", async () =>
        {
            var directory = Path.Combine(root, "injection"); var app = new ApplicationServices(Path.Combine(directory, "catalog"), discoveryProviders: []);
            Assert((await app.Discovery.RefreshAsync()).Projects == 0, "Fixture provider injection read user configuration.");
        });
    }
}
