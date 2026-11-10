using RepoBackup.Core.Application;
using RepoBackup.Core.Backup;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class LongGitPathTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        await tests.Run("Long tracked paths back up and restore despite a disabled local longpaths setting", async () =>
        {
            var source = Path.Combine(root, "long-source");
            Directory.CreateDirectory(source);
            var process = new ProcessRunner();
            async Task Git(params string[] args) => (await process.RunAsync(BundledTools.GitExecutable,
                new[] { "-c", "core.longpaths=true", "-C", source }.Concat(args))).EnsureSuccess("Prepare long Git fixture");
            await Git("init", "-b", "main"); await Git("config", "user.name", "Backup Test"); await Git("config", "user.email", "backup-test@example.invalid");
            var relative = Path.Combine(new string('a', 80), new string('b', 80), new string('c', 80), "tracked.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(source, relative))!);
            await File.WriteAllTextAsync(Path.Combine(source, relative), "long source path");
            await Git("add", "."); await Git("commit", "-m", "Long path fixture"); await Git("config", "core.longpaths", "false");
            var app = new ApplicationServices(Path.Combine(root, "long-catalog"), discoveryProviders: []);
            var project = app.Catalog.RegisterCandidate("Long Git source", [@"\\?\" + source], "Fixture", approved: true);
            var destination = await app.Backups.AddDestinationAsync("Long Git fixture", Path.Combine(root, "long-repository"), protection: DestinationProtection.PasswordFree);
            var job = (await app.Backups.RunAsync(destination, [project])).Single();
            Assert(RunOutcome.IsSuccessful(job), Json.Write(job));
            var snapshot = (await app.Restic.SnapshotSummariesAsync(destination)).Single();
            var manifest = await app.Restic.ManifestAsync(destination, snapshot);
            var target = Path.Combine(root, "long-restored"); await app.Restore.RestoreAsync(destination, snapshot, target);
            Assert(await File.ReadAllTextAsync(Path.Combine(target, "sources", manifest.Sources.Single().RestoreFolder, relative)) == "long source path",
                "Long source contents were not restored.");
        });
    }
}
