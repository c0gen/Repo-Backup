using Microsoft.Data.Sqlite;
using RepoBackup.Core.Discovery;
using RepoBackup.Core.Infrastructure;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class CodexDiscoveryTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        await tests.Run("Codex extension-only database discovery preserves named workspaces and excludes subagents and messages", async () =>
        {
            var directory = Path.Combine(root, "metadata"); var first = DiscoveryFixtures.Folder(directory, "first");
            var second = DiscoveryFixtures.Folder(directory, "second"); var cli = DiscoveryFixtures.Folder(directory, "cli");
            var excluded = DiscoveryFixtures.Folder(directory, "subagent"); var home = Path.Combine(directory, "codex");
            var db = ExtensionDiscoveryFixtures.CodexDatabase(home, 5,
                [(first, "vscode"), (cli, "cli"), (excluded, "{\"subagent\":{\"thread_spawn\":{}}}"), (excluded, "exec"), ("/home/remote", "vscode"), ("vscode-remote://ssh-remote+host/project", "vscode")],
                [new("workspace-id", "Named workspace", [first, second])]);
            var hash = await Fixture.HashAsync(db); var catalog = DiscoveryFixtures.Catalog(directory);
            var result = await new ProjectDiscoveryService(catalog, [new CodexDiscovery(homeDirectory: home)]).RefreshAsync();
            Assert(result is { Added: 2, Projects: 2, Roots: 3 } && result.Warnings.Count == 0, Json.Write(result));
            var grouped = catalog.Projects().Single(p => p.Roots.Count == 2);
            Assert(grouped.Name == "Named workspace" && grouped.Roots.Select(r => r.Path).ToHashSet().SetEquals([first, second]), "Named saved project did not take precedence over its thread folder.");
            Assert(catalog.Projects().All(p => !p.Enabled && !p.Reviewed && p.DiscoverySources.All(s => s.ProviderId == DiscoveryProviders.Codex)), "Extension discovery enrolled a project or used a separate Codex label.");
            Assert(await Fixture.HashAsync(db) == hash && !catalog.Export().Contains("fixture-private-data"), "Codex metadata changed or conversation fields entered the catalog.");
        });

        await tests.Run("Codex database versions are selected numerically and refresh discovers new metadata", async () =>
        {
            var directory = Path.Combine(root, "versions"); var older = DiscoveryFixtures.Folder(directory, "older"); var latest = DiscoveryFixtures.Folder(directory, "latest");
            var home = Path.Combine(directory, "codex"); ExtensionDiscoveryFixtures.CodexDatabase(home, 9, [(older, "vscode")]);
            ExtensionDiscoveryFixtures.CodexDatabase(home, 10, [(latest, "vscode")]);
            await File.WriteAllTextAsync(Path.Combine(home, "state_not-a-version.sqlite"), "private-invalid-fixture");
            var provider = new CodexDiscovery(homeDirectory: home); var result = await provider.ReadAsync();
            Assert(result.Projects.Single().Roots.Single() == latest && result.Warnings.Count == 0, "Lexical or non-versioned metadata selected.");
            ExtensionDiscoveryFixtures.CodexDatabase(home, 11, [(older, "cli")]);
            Assert((await provider.ReadAsync()).Projects.Single().Roots.Single() == older, "Metadata was cached across refreshes.");
        });

        await tests.Run("Codex registry overrides remain isolated unless a home directory opts in", async () =>
        {
            var directory = Path.Combine(root, "overrides"); var registryFolder = DiscoveryFixtures.Folder(directory, "registry"); var extensionFolder = DiscoveryFixtures.Folder(directory, "extension");
            var state = await DiscoveryFixtures.CodexState(directory, registryFolder); var home = Path.Combine(directory, "codex");
            ExtensionDiscoveryFixtures.CodexDatabase(home, 5, [(extensionFolder, "vscode")]);
            var previous = Environment.GetEnvironmentVariable("CODEX_HOME");
            try
            {
                Environment.SetEnvironmentVariable("CODEX_HOME", home);
                Assert((await new CodexDiscovery(state).ReadAsync()).Projects.Single().Roots.Single() == registryFolder, "Explicit registry leaked default session metadata.");
                var combined = await new CodexDiscovery(state, home).ReadAsync();
                Assert(combined.Projects.Count == 2 && combined.Warnings.Count == 0, "Explicit home did not combine registry and session metadata.");
            }
            finally { Environment.SetEnvironmentVariable("CODEX_HOME", previous); }
            var providers = ProjectDiscoveryService.CreateProviders(new() { Source = DiscoveryProviders.Codex, CodexStatePath = state });
            Assert(providers.Count == 1 && providers[0] is CodexDiscovery, "Registry-only discovery scanned VS Code.");
        });

        await tests.Run("Codex keeps different folder observations under a shared desktop and database project ID", async () =>
        {
            var directory = Path.Combine(root, "shared-id"); var desktop = DiscoveryFixtures.Folder(directory, "desktop"); var extension = DiscoveryFixtures.Folder(directory, "extension");
            var state = await DiscoveryFixtures.CodexState(directory, desktop); var home = Path.Combine(directory, "codex");
            ExtensionDiscoveryFixtures.CodexDatabase(home, 5, [], [new("shared-id", "New project location", [extension])]);
            var catalog = DiscoveryFixtures.Catalog(directory); var service = new ProjectDiscoveryService(catalog, [new CodexDiscovery(state, home)]);
            var result = await service.RefreshAsync(); var again = await service.RefreshAsync();
            Assert(result is { Added: 2, Projects: 2, Roots: 2 } && again.Added == 0 && catalog.Projects().All(p => p.DiscoverySources.Single().ExternalId == "shared-id"), "External ID dropped a folder observation or duplicated a refresh.");
        });

        await tests.Run("Corrupt Codex database preserves registry candidates and permits another provider", async () =>
        {
            var directory = Path.Combine(root, "corrupt"); var folder = DiscoveryFixtures.Folder(directory, "source"); var home = DiscoveryFixtures.Folder(directory, "codex");
            var state = await DiscoveryFixtures.CodexState(directory, folder); var db = Path.Combine(home, "state_5.sqlite"); await File.WriteAllTextAsync(db, "fixture-private-invalid-db");
            var userData = Path.Combine(directory, "vscode"); await ExtensionDiscoveryFixtures.Workspace(userData, "workspace", folder);
            var catalog = DiscoveryFixtures.Catalog(directory); var hash = await Fixture.HashAsync(db);
            var result = await new ProjectDiscoveryService(catalog, [new CodexDiscovery(state, home), new VsCodeDiscovery(userData)]).RefreshAsync();
            Assert(result.Added == 1 && result.Warnings.Count == 1 && catalog.Projects().Single().DiscoverySources.Count == 2, Json.Write(result));
            Assert(await Fixture.HashAsync(db) == hash && !string.Join(" ", result.Warnings).Contains("fixture-private"), "Corrupt metadata changed or leaked.");
        });

        await tests.Run("Unsupported Codex project schema still permits interactive thread metadata", async () =>
        {
            var directory = Path.Combine(root, "schema"); var folder = DiscoveryFixtures.Folder(directory, "source"); var home = Path.Combine(directory, "codex");
            var path = ExtensionDiscoveryFixtures.CodexDatabase(home, 5, [(folder, "vscode")]);
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            { db.Open(); using var command = db.CreateCommand(); command.CommandText = "ALTER TABLE project_roots RENAME COLUMN path TO changed_path"; command.ExecuteNonQuery(); }
            var result = await new CodexDiscovery(homeDirectory: home).ReadAsync();
            Assert(result.Projects.Single().Roots.Single() == folder && result.Warnings.Count == 1, Json.Write(result));
        });

        await tests.Run("Codex read-only discovery sees committed WAL metadata without modifying source files", async () =>
        {
            var directory = Path.Combine(root, "wal"); var folder = DiscoveryFixtures.Folder(directory, "source"); var home = Path.Combine(directory, "codex");
            var path = ExtensionDiscoveryFixtures.CodexDatabase(home, 5, []);
            using var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); writer.Open();
            using var command = writer.CreateCommand(); command.CommandText = "PRAGMA journal_mode=WAL"; command.ExecuteScalar();
            command.CommandText = "INSERT INTO threads(cwd,source) VALUES($cwd,'vscode')"; command.Parameters.AddWithValue("$cwd", folder); command.ExecuteNonQuery();
            var hash = await ExtensionDiscoveryFixtures.SharedHashAsync(path); var walHash = await ExtensionDiscoveryFixtures.SharedHashAsync(path + "-wal");
            var result = await new CodexDiscovery(homeDirectory: home).ReadAsync();
            Assert(result.Projects.Single().Roots.Single() == folder && result.Warnings.Count == 0, "Committed WAL records were lost.");
            Assert(await ExtensionDiscoveryFixtures.SharedHashAsync(path) == hash && await ExtensionDiscoveryFixtures.SharedHashAsync(path + "-wal") == walHash, "Discovery modified source database or WAL.");
        });

        await tests.Run("Locked Codex metadata warns and missing installations and cancellation stay bounded", async () =>
        {
            var directory = Path.Combine(root, "locked"); var folder = DiscoveryFixtures.Folder(directory, "source"); var home = Path.Combine(directory, "codex");
            var path = ExtensionDiscoveryFixtures.CodexDatabase(home, 5, [(folder, "vscode")]);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var result = await new CodexDiscovery(homeDirectory: home).ReadAsync();
                Assert(result.Projects.Count == 0 && result.Warnings.Count == 1, "Locked metadata did not warn.");
            }
            var missing = await new CodexDiscovery(homeDirectory: Path.Combine(directory, "absent")).ReadAsync();
            Assert(missing.Projects.Count == 0 && missing.Warnings.Count == 0, "Missing installation generated a warning.");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Throws<OperationCanceledException>(async () => await new CodexDiscovery(homeDirectory: home).ReadAsync(cancelled.Token));
        });
    }
}
