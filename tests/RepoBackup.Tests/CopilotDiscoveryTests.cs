using System.Text;
using RepoBackup.Core.Discovery;
using RepoBackup.Core.Infrastructure;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class CopilotDiscoveryTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        await tests.Run("Copilot discovers only its first session metadata record and ignores conversation bodies", async () =>
        {
            var directory = Path.Combine(root, "header"); var folder = DiscoveryFixtures.Folder(directory, "source 雪"); var userData = Path.Combine(directory, "vscode");
            var storage = await ExtensionDiscoveryFixtures.Workspace(userData, "workspace", folder);
            var transcripts = ExtensionDiscoveryFixtures.TranscriptDirectory(storage); var transcript = Path.Combine(transcripts, "session.jsonl");
            await File.WriteAllTextAsync(transcript, ExtensionDiscoveryFixtures.CopilotHeader(folder) + "\r\ninvalid-fixture-private-conversation\n", new UTF8Encoding(true));
            var hash = await Fixture.HashAsync(transcript); var catalog = DiscoveryFixtures.Catalog(directory);
            var result = await new ProjectDiscoveryService(catalog, [new VsCodeDiscovery(userData)]).RefreshAsync();
            Assert(result.Added == 1 && result.Warnings.Count == 0 && catalog.Projects().Single().DiscoverySources.Select(s => s.ProviderId).ToHashSet().SetEquals([DiscoveryProviders.VsCode, DiscoveryProviders.Copilot]), Json.Write(result));
            Assert(await Fixture.HashAsync(transcript) == hash && !catalog.Export().Contains("fixture-private"), "Transcript modified or conversation/session IDs persisted.");
            var selected = await new VsCodeDiscovery(userData, DiscoveryProviders.Copilot).ReadAsync();
            Assert(selected.Projects.Single().Roots.Single() == folder && selected.Projects.Single().AssociatedProviders.Count == 0, "Copilot filter lost the metadata folder.");
        });

        await tests.Run("Copilot metadata discovers empty-window working folders without scanning subsequent records", async () =>
        {
            var directory = Path.Combine(root, "empty-window"); var folder = DiscoveryFixtures.Folder(directory, "source"); var userData = Path.Combine(directory, "vscode");
            var storage = DiscoveryFixtures.Folder(userData, "User\\workspaceStorage\\empty-window");
            var transcripts = ExtensionDiscoveryFixtures.TranscriptDirectory(storage);
            await File.WriteAllTextAsync(Path.Combine(transcripts, "valid.jsonl"), ExtensionDiscoveryFixtures.CopilotHeader(folder) + "\n");
            await File.WriteAllTextAsync(Path.Combine(transcripts, "other-producer.jsonl"), ExtensionDiscoveryFixtures.CopilotHeader(Path.Combine(directory, "excluded"), "another-agent") + "\n");
            await File.WriteAllTextAsync(Path.Combine(transcripts, "remote.jsonl"), ExtensionDiscoveryFixtures.CopilotHeader("vscode-remote://ssh-remote+host/project") + "\n");
            await File.WriteAllTextAsync(Path.Combine(transcripts, "later-record.jsonl"), "{\"type\":\"user.message\",\"data\":\"fixture-private-message\"}\n" + ExtensionDiscoveryFixtures.CopilotHeader(Path.Combine(directory, "excluded")));
            var result = await new VsCodeDiscovery(userData, DiscoveryProviders.Copilot).ReadAsync();
            Assert(result.Projects.Single().Roots.Single() == folder && result.Warnings.Count == 0, Json.Write(result));
        });

        await tests.Run("Copilot accepts large transcript files but bounds each first metadata record to one MiB", async () =>
        {
            var directory = Path.Combine(root, "bounds"); var folder = DiscoveryFixtures.Folder(directory, "source"); var userData = Path.Combine(directory, "vscode");
            var storage = await ExtensionDiscoveryFixtures.Workspace(userData, "workspace", folder); var transcripts = ExtensionDiscoveryFixtures.TranscriptDirectory(storage);
            var large = Path.Combine(transcripts, "large-body.jsonl");
            await using (var stream = new FileStream(large, FileMode.Create, FileAccess.Write))
            { await stream.WriteAsync(Encoding.UTF8.GetBytes(ExtensionDiscoveryFixtures.CopilotHeader(folder) + "\n")); stream.SetLength(40 * 1024 * 1024); }
            await File.WriteAllTextAsync(Path.Combine(transcripts, "oversized-header.jsonl"), new string(' ', 1024 * 1024 + 1) + ExtensionDiscoveryFixtures.CopilotHeader(Path.Combine(directory, "excluded")));
            var result = await new VsCodeDiscovery(userData, DiscoveryProviders.Copilot).ReadAsync();
            Assert(result.Projects.Single().Roots.Single() == folder && result.Warnings.Count == 1 && result.Warnings[0].Contains("session metadata"), "Whole transcript size or unbounded header affected discovery.");
        });

        await tests.Run("Malformed and locked Copilot headers preserve ordinary folders and independent extension state", async () =>
        {
            var directory = Path.Combine(root, "failures"); var folder = DiscoveryFixtures.Folder(directory, "source"); var userData = Path.Combine(directory, "vscode");
            var storage = await ExtensionDiscoveryFixtures.Workspace(userData, "workspace", folder, new Dictionary<string, string> { ["GitHub.copilot-chat"] = "fixture-private-state" });
            var transcripts = ExtensionDiscoveryFixtures.TranscriptDirectory(storage); var lockedFile = Path.Combine(transcripts, "locked.jsonl");
            await File.WriteAllTextAsync(lockedFile, ExtensionDiscoveryFixtures.CopilotHeader(folder));
            await File.WriteAllTextAsync(Path.Combine(transcripts, "invalid.jsonl"), "fixture-private-invalid-json\n");
            using var locked = new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var catalog = DiscoveryFixtures.Catalog(directory); var result = await new ProjectDiscoveryService(catalog, [new VsCodeDiscovery(userData)]).RefreshAsync();
            Assert(result.Added == 1 && result.Warnings.Count == 2 && catalog.Projects().Single().DiscoverySources.Count == 2, Json.Write(result));
            Assert(!catalog.Export().Contains("fixture-private") && !string.Join(" ", result.Warnings).Contains("fixture-private"), "Copilot state or parse errors leaked private content.");
        });
    }
}
