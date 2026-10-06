using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using RepoBackup.Core.Discovery;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using RepoBackup.Core.Storage;
using RepoBackup.Core.Windows;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class HookTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        await tests.Run("Claude settings keep unrelated handlers and install one asynchronous SessionStart hook", () =>
        {
            const string existing = """{"permissions":{"allow":["fixture-command"]},"hooks":{"Stop":[{"hooks":[{"type":"command","command":"keep-stop"}]}],"SessionStart":[{"matcher":"clear","hooks":[{"type":"command","command":"keep-start"}]}]}}""";
            var merged = ClaudeCodeHookInstaller.Merge(existing, "C:\\Program Files\\Fixture\\RepoBackup.Runner.exe", root);
            var document = JsonNode.Parse(merged)!;
            Assert(document["permissions"]!.ToJsonString() == JsonNode.Parse(existing)!["permissions"]!.ToJsonString(), "Unrelated settings changed.");
            Assert(merged.Contains("keep-stop") && merged.Contains("keep-start"), "Existing handlers lost.");
            var handler = document["hooks"]!["SessionStart"]!.AsArray().Last()!["hooks"]!.AsArray().Single()!;
            Assert(handler["async"]!.GetValue<bool>() && handler["timeout"]!.GetValue<int>() == 5, "Claude hook is not asynchronous and bounded.");
            Assert(merged == ClaudeCodeHookInstaller.Merge(merged, "C:\\Program Files\\Fixture\\RepoBackup.Runner.exe", root), "Claude installation duplicated a hook.");
            return Task.CompletedTask;
        });

        await tests.Run("Named Antigravity PostInvocation hook preserves other names without assuming async support", async () =>
        {
            const string existing = """{"team-hook":{"disabled":true,"PostInvocation":[{"type":"command","command":"keep-team","timeout":17}]}}""";
            var merged = AntigravityHookInstaller.Merge(existing, "C:\\Fixture\\RepoBackup.Runner.exe", root);
            var document = JsonNode.Parse(merged)!; var handler = document["repo-backup-discovery"]!["PostInvocation"]!.AsArray().Single()!;
            Assert(document["team-hook"]!.ToJsonString() == JsonNode.Parse(existing)!["team-hook"]!.ToJsonString(), "Unrelated named hook changed.");
            Assert(handler["async"] is null && handler["timeout"]!.GetValue<int>() == 5 && document["hooks"] is null, "Incorrect Antigravity hook schema.");
            Assert(merged == AntigravityHookInstaller.Merge(merged, "C:\\Fixture\\RepoBackup.Runner.exe", root), "Antigravity installation duplicated a hook.");
            await Throws<InvalidDataException>(() => { AntigravityHookInstaller.Merge("{\"repo-backup-discovery\":{\"PostInvocation\":[]}}", "C:\\Fixture\\RepoBackup.Runner.exe", root); return Task.CompletedTask; });
        });

        await tests.Run("Hook configuration writes make one backup, preserve invalid files and wait for verification", async () =>
        {
            var directory = DiscoveryFixtures.Folder(root, "configuration"); var runner = NativeProcessFixture.ProgramPath("RepoBackup.Runner");
            foreach (var provider in DiscoveryProviders.HookCapable)
            {
                var config = Path.Combine(directory, provider + ".json"); const string original = "{\"fixture-unrelated\":{\"enabled\":false}}";
                await File.WriteAllTextAsync(config, original); var paths = new AppPaths(Path.Combine(directory, provider, "catalog")); var catalog = new CatalogStore(paths);
                var installation = DiscoveryHookInstaller.Install(provider, catalog, paths.DataDirectory, runner, config);
                Assert(DiscoveryHookInstaller.Status(catalog, provider).StartsWith("Configured"), "Installation falsely reported a verified hook.");
                var first = await File.ReadAllTextAsync(config); var backups = Directory.GetFiles(directory, provider + ".json.repobackup-*.bak");
                Assert(backups.Length == 1 && await File.ReadAllTextAsync(backups[0]) == original, "Original settings backup missing.");
                var again = DiscoveryHookInstaller.Install(provider, catalog, paths.DataDirectory, runner, config);
                Assert(again.ConfiguredAt == installation.ConfiguredAt && await File.ReadAllTextAsync(config) == first && Directory.GetFiles(directory, provider + ".json.repobackup-*.bak").Length == 1, "Repeat install changed configuration or verification epoch.");
                catalog.SaveSetting("hook-last-registration:" + provider, DateTimeOffset.UtcNow);
                Assert(DiscoveryHookInstaller.Status(catalog, provider).StartsWith("Verified"), "Successful callback not verified.");
            }
            var invalid = Path.Combine(directory, "invalid.json"); await File.WriteAllTextAsync(invalid, "{private-invalid-fixture");
            await Throws<JsonException>(() => { HookConfigurationWriter.Update(invalid, text => ClaudeCodeHookInstaller.Merge(text, runner, directory)); return Task.CompletedTask; });
            Assert(await File.ReadAllTextAsync(invalid) == "{private-invalid-fixture" && Directory.GetFiles(directory, "invalid.json.repobackup-*.bak").Length == 0, "Invalid config was overwritten.");
            Assert(Directory.GetFiles(directory, "*.tmp").Length == 0, "Temporary configuration files were left behind.");
        });

        await tests.Run("Legacy Codex installation upgrades stdin forwarding without a duplicate handler", () =>
        {
            const string runner = "C:\\Fixture\\RepoBackup.Runner.exe";
            var command = "powershell.exe -NoLogo -NoProfile -NonInteractive -Command \"& '" + runner + "' register-hook\"";
            var config = Json.Write(new { hooks = new { SessionStart = new[] { new { matcher = "startup|resume", hooks = new[] { new { type = "command", command, timeout = 30 } } } } } });
            var merged = JsonNode.Parse(CodexHookInstaller.Merge(config, runner, root))!;
            var handlers = merged["hooks"]!["SessionStart"]!.AsArray().SelectMany(g => g!["hooks"]!.AsArray()).ToList();
            Assert(handlers.Count == 1 && handlers[0]!["command"]!.GetValue<string>().Contains("-EncodedCommand") && handlers[0]!["async"]!.GetValue<bool>() && handlers[0]!["timeout"]!.GetValue<int>() == 5, "Legacy hook was duplicated or left unbounded.");
            return Task.CompletedTask;
        });

        await tests.Run("Hook input registers metadata only, preserves dismissal and never starts or repairs jobs", async () =>
        {
            var directory = Path.Combine(root, "registration"); var first = DiscoveryFixtures.Folder(directory, "first"); var second = DiscoveryFixtures.Folder(directory, "second");
            var paths = new AppPaths(Path.Combine(directory, "catalog")); var catalog = new CatalogStore(paths); var project = catalog.RegisterCandidate("Dismissed", [first], "Manual");
            catalog.SaveProject(project with { Dismissed = true, Reviewed = true });
            var job = new JobRecord { ProjectId = project.Id, ProjectName = project.Name, DestinationId = "fixture", SeriesId = "fixture", OwnerProcessId = int.MaxValue, OwnerStartTicks = 1 }; catalog.SaveJob(job);
            foreach (var provider in DiscoveryProviders.HookCapable)
            {
                var json = provider == DiscoveryProviders.Antigravity
                    ? Json.Write(new { workspacePaths = new[] { first, second }, userPrompt = "private-fixture-prompt" })
                    : Json.Write(new { cwd = first, transcript_path = "private-fixture-transcript" });
                Assert(await HookRegistration.RegisterAsync(provider, paths, new StringReader(json)), "Hook registration failed.");
            }
            var updated = catalog.Projects().Single(p => p.Id == project.Id);
            Assert(updated.Dismissed && !updated.Enabled && updated.Roots.Count == 1 && updated.DiscoverySources.Count == 4, "Callback changed review or existing roots.");
            Assert(catalog.Projects().Single(p => p.Id != project.Id) is { Reviewed: false, Enabled: false }, "Hook enrolled an unmatched root.");
            Assert(catalog.History().Single().Backup == Outcome.Running && catalog.Destinations().Count == 0 && catalog.Schedules().Count == 0, "Hook ran backup or job recovery work.");
            Assert(!catalog.Export().Contains("private-fixture"), "Raw hook contents entered the catalog.");
        });

        await tests.Run("Invalid and busy hook callbacks fail quietly within a bounded wait", async () =>
        {
            var directory = Path.Combine(root, "failure"); var paths = new AppPaths(Path.Combine(directory, "catalog")); var catalog = new CatalogStore(paths); var folder = DiscoveryFixtures.Folder(directory, "source");
            var before = catalog.Export();
            Assert(!await HookRegistration.RegisterAsync(DiscoveryProviders.ClaudeCode, paths, new StringReader("{private-fixture-payload")), "Malformed hook succeeded.");
            Assert(!await HookRegistration.RegisterAsync(DiscoveryProviders.ClaudeCode, paths, new StringReader(new string('x', 1024 * 1024 + 1))), "Oversize hook succeeded.");
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath }.ToString()); db.Open();
            using (var transaction = db.BeginTransaction(deferred: false))
            {
                var watch = Stopwatch.StartNew();
                Assert(!await HookRegistration.RegisterAsync(DiscoveryProviders.ClaudeCode, paths, new StringReader(Json.Write(new { cwd = folder }))), "Busy hook unexpectedly succeeded.");
                Assert(watch.Elapsed < TimeSpan.FromSeconds(5), "Busy hook exceeded its wait budget.");
            }
            var log = await File.ReadAllTextAsync(Path.Combine(paths.DataDirectory, "hook-errors.log"));
            Assert(catalog.Export() == before && !log.Contains("private-fixture"), "Failed hook changed catalog or leaked payload.");
        });

        await tests.Run("Native PowerShell hook commands forward Unicode stdin and catalog paths to the hidden runner", async () =>
        {
            var directory = Path.Combine(root, "native ' 雪 $literal; (paths)"); var source = DiscoveryFixtures.Folder(directory, "source ' 雪");
            var runnerDirectory = DiscoveryFixtures.Folder(root, "runner ' 雪 $literal (paths)");
            var originalRunner = NativeProcessFixture.ProgramPath("RepoBackup.Runner");
            var originalDirectory = Path.GetDirectoryName(originalRunner)!;
            foreach (var file in Directory.EnumerateFiles(originalDirectory, "*", SearchOption.AllDirectories))
            {
                var copy = Path.Combine(runnerDirectory, Path.GetRelativePath(originalDirectory, file));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!); File.Copy(file, copy);
            }
            var runner = Path.Combine(runnerDirectory, "RepoBackup.Runner.exe"); var paths = new AppPaths(Path.Combine(directory, "catalog")); var catalog = new CatalogStore(paths);
            var directPaths = new AppPaths(Path.Combine(directory, "direct-catalog"));
            var direct = await NativeProcessFixture.RunAsync(runner, ["register-hook", "--source", DiscoveryProviders.Codex, "--data-dir", directPaths.DataDirectory], Json.Write(new { cwd = source }));
            Assert(direct.ExitCode == 0 && new CatalogStore(directPaths).Projects().Count == 1, "Hidden runner did not receive direct stdin.");
            foreach (var provider in DiscoveryProviders.HookCapable)
            {
                var json = JsonSerializer.Serialize(provider == DiscoveryProviders.Antigravity ? (object)new { workspacePaths = new[] { source }, userPrompt = "private-fixture 雪" } : new { cwd = source, transcript_path = "private-fixture 雪" }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                var command = HookCommandBuilder.Build(runner, provider, paths.DataDirectory);
                await File.WriteAllTextAsync(Path.Combine(directory, provider + "-command.txt"), command);
                var encoded = command[(command.IndexOf("-EncodedCommand ", StringComparison.Ordinal) + "-EncodedCommand ".Length)..];
                var result = await NativeProcessFixture.RunAsync("powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded], json);
                Assert(result.ExitCode == 0 && result.Output.Length == 0 && result.Error.Length == 0, "Native hook emitted context, a permission decision, or an error: " + Json.Write(result));
                var logPath = Path.Combine(paths.DataDirectory, "hook-errors.log");
                Assert(DiscoveryHookInstaller.Status(catalog, provider).StartsWith("Verified"), "Native stdin did not reach the registrar for " + provider + (File.Exists(logPath) ? ": " + await File.ReadAllTextAsync(logPath) : ". No callback received."));
            }
            var project = catalog.Projects().Single(); Assert(project.Roots.Single().Path == source && project.DiscoverySources.Count == 3 && !project.Reviewed && !project.Enabled, "Unicode forwarding or shared identity failed.");
            Assert(catalog.History().Count == 0 && catalog.Destinations().Count == 0 && !catalog.Export().Contains("private-fixture"), "Native hook saved private content or ran a backup.");
            var failed = await NativeProcessFixture.RunAsync(runner, ["register-hook", "--data-dir", paths.DataDirectory, "--source", DiscoveryProviders.ClaudeCode], "{private-invalid-fixture");
            Assert(failed.ExitCode == 0 && failed.Output.Length == 0 && failed.Error.Length == 0, "Hidden runner did not fail open.");
        });
    }
}
