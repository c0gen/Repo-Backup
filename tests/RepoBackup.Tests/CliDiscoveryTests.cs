using RepoBackup.Core.Discovery;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using RepoBackup.Core.Storage;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class CliDiscoveryTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        await tests.Run("CLI discover defaults to all providers and honors isolated path overrides", async () =>
        {
            var directory = Path.Combine(root, "all"); var folder = DiscoveryFixtures.Folder(directory, "source");
            var state = await DiscoveryFixtures.CodexState(directory, folder); var config = await DiscoveryFixtures.ClaudeState(directory, folder);
            var indexes = DiscoveryFixtures.Folder(directory, "empty-indexes");
            var userData = DiscoveryFixtures.AntigravityDatabase(directory, new Dictionary<string, string>
            { ["antigravityUnifiedStateSync.sidebarWorkspaces"] = DiscoveryFixtures.Sidebar(DiscoveryFixtures.FileUri(folder)) });
            var paths = new AppPaths(Path.Combine(directory, "catalog"));
            var result = await Run("discover", "--data-dir", paths.DataDirectory, "--codex-state", state, "--claude-config", config, "--claude-projects", indexes, "--antigravity-user-data", userData);
            result.EnsureSuccess("CLI discover all"); var discovery = Json.Read<DiscoveryResult>(result.Output);
            Assert(discovery is { Added: 1, Projects: 1, Roots: 1 } && discovery.Warnings.Count == 0 && result.Error.Length == 0, Json.Write(result));
            var project = new CatalogStore(paths).Projects().Single(); Assert(project.DiscoverySources.Count == 3 && !project.Enabled && !project.Reviewed, "CLI skipped a provider or enrolled a candidate.");
            var programs = await Run("projects", "--data-dir", paths.DataDirectory); programs.EnsureSuccess("CLI project labels");
            Assert(Json.Read<List<ProjectEntry>>(programs.Output).Single().Id == project.Id, "CLI project identity changed.");
        });

        await tests.Run("CLI provider selection and legacy state option keep Codex-only compatibility", async () =>
        {
            var directory = Path.Combine(root, "source"); var codexFolder = DiscoveryFixtures.Folder(directory, "codex"); var claudeFolder = DiscoveryFixtures.Folder(directory, "claude");
            var state = await DiscoveryFixtures.CodexState(directory, codexFolder); var config = await DiscoveryFixtures.ClaudeState(directory, claudeFolder);
            var indexes = DiscoveryFixtures.Folder(directory, "empty-indexes"); var paths = new AppPaths(Path.Combine(directory, "catalog"));
            (await Run("discover", "--data-dir", paths.DataDirectory, "--source", DiscoveryProviders.ClaudeCode, "--claude-config", config, "--claude-projects", indexes, "--codex-state", state)).EnsureSuccess("Select Claude discovery");
            var catalog = new CatalogStore(paths); Assert(catalog.Projects().Single().Roots.Single().Path == claudeFolder, "--source selection scanned Codex.");
            (await Run("discover", "--data-dir", paths.DataDirectory, "--state", state)).EnsureSuccess("Legacy Codex discovery");
            Assert(catalog.Projects().Count == 2 && catalog.Projects().All(p => p.DiscoverySources.Count == 1), "Legacy state option scanned real providers.");
            var before = catalog.Export(); var rejected = await Run("discover", "--data-dir", paths.DataDirectory, "--state", state, "--source", "all");
            Assert(rejected.ExitCode == 1 && rejected.Output.Length == 0 && rejected.Error.Contains("Codex-only") && catalog.Export() == before, "Ambiguous legacy option changed catalog.");
            rejected = await Run("discover", "--data-dir", paths.DataDirectory, "--source", "unknown");
            Assert(rejected.ExitCode == 1 && catalog.Export() == before, "Unknown source accepted or changed catalog.");
        });

        await tests.Run("CLI manual registration shares folder identity with discovered candidates", async () =>
        {
            var directory = Path.Combine(root, "manual"); var folder = DiscoveryFixtures.Folder(directory, "source"); var state = await DiscoveryFixtures.CodexState(directory, folder);
            var paths = new AppPaths(Path.Combine(directory, "catalog"));
            (await Run("discover", "--data-dir", paths.DataDirectory, "--state", state)).EnsureSuccess("Discover manual fixture");
            var catalog = new CatalogStore(paths); var project = catalog.Projects().Single();
            (await Run("register", "--data-dir", paths.DataDirectory, "--path", folder.ToUpperInvariant().Replace('\\', '/'), "--name", "Different name")).EnsureSuccess("Register same folder");
            Assert(catalog.Projects().Single().Id == project.Id && catalog.Projects().Single().Name == project.Name && !catalog.Projects().Single().Reviewed, "Manual registration duplicated or reset discovery review.");
        });

        await tests.Run("CLI hook commands retain Codex defaults, quiet failure and metadata-only registration", async () =>
        {
            var directory = Path.Combine(root, "hook-default"); var folder = DiscoveryFixtures.Folder(directory, "source 雪"); var paths = new AppPaths(Path.Combine(directory, "catalog"));
            var cli = NativeProcessFixture.ProgramPath("RepoBackup.Cli");
            var result = await NativeProcessFixture.RunAsync(cli, ["register-hook", "--data-dir", paths.DataDirectory], Json.Write(new { cwd = folder, transcript_path = "private-fixture" }));
            Assert(result.ExitCode == 0 && result.Output.Length == 0 && result.Error.Length == 0, "Default hook emitted output.");
            var catalog = new CatalogStore(paths); var project = catalog.Projects().Single(); Assert(project.DiscoverySources.Single().ProviderId == DiscoveryProviders.Codex && !project.Enabled && !project.Reviewed, "Default hook changed to another provider or enrolled project.");
            result = await NativeProcessFixture.RunAsync(cli, ["register-hook", "--data-dir", paths.DataDirectory, "--source", DiscoveryProviders.ClaudeCode], "{private-invalid-fixture");
            Assert(result.ExitCode == 0 && result.Output.Length == 0 && result.Error.Length == 0 && catalog.Projects().Single().DiscoverySources.Count == 1, "Registration failure escaped native hook.");
            Assert(catalog.History().Count == 0 && !catalog.Export().Contains("private-fixture"), "CLI hook started backup or saved raw input.");
        });
    }

    private static Task<ProcessResult> Run(params string[] arguments) => NativeProcessFixture.RunAsync(NativeProcessFixture.ProgramPath("RepoBackup.Cli"), arguments);
}
