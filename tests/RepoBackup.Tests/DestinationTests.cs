using System.Text.Json.Nodes;
using RepoBackup.Core.Application;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class DestinationTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        var app = new ApplicationServices(Path.Combine(root, "catalog"), discoveryProviders: []);
        await tests.Run("Legacy destination payloads and imports retain recovery-key protection", () =>
        {
            var legacy = Json.Read<Destination>("{\"name\":\"Legacy\",\"path\":\"C:\\\\Backup\"}");
            Assert(legacy.Protection == DestinationProtection.RecoveryKey, "Legacy destination lost protection.");
            foreach (var version in new[] { 1, 2 })
            {
                var imported = new ApplicationServices(Path.Combine(root, "legacy-" + version), discoveryProviders: []);
                var document = JsonNode.Parse(Json.Write(new CatalogExport(version, [], [], [legacy], [], [])))!;
                document["destinations"]![0]!.AsObject().Remove("protection");
                imported.Catalog.Import(document.ToJsonString());
                Assert(imported.Catalog.Destinations().Single().Protection == DestinationProtection.RecoveryKey, "Legacy import became password-free.");
            }
            return Task.CompletedTask;
        });
        await tests.Run("Password-free commands ignore inherited passwords, files, commands and key hints", async () =>
        {
            var names = new[] { "RESTIC_PASSWORD", "RESTIC_PASSWORD_FILE", "RESTIC_PASSWORD_COMMAND", "RESTIC_KEY_HINT" };
            var original = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(names[0], "ambient-fixture-password");
                Environment.SetEnvironmentVariable(names[1], Path.Combine(root, "nonexistent-key-file"));
                Environment.SetEnvironmentVariable(names[2], "nonexistent-fixture-password-command");
                Environment.SetEnvironmentVariable(names[3], new string('0', 64));
                var destination = await app.Backups.AddDestinationAsync("Password-free", Path.Combine(root, "password-free"), protection: DestinationProtection.PasswordFree);
                Assert((await app.Restic.SnapshotsAsync(destination)).Count == 0, "New repository was not empty.");
                Assert((await app.Backups.VerifyAsync(destination)).Outcome == Outcome.Successful, "Ambient credentials interfered with verification.");
                var reopened = await app.Backups.AddDestinationAsync("Reopened", destination.Path, openExisting: true, protection: DestinationProtection.PasswordFree);
                Assert(reopened.Id == destination.Id && !Directory.EnumerateFiles(app.Paths.CredentialsDirectory).Any(), "Password-free operations generated credentials or duplicated the destination.");
                var imported = new ApplicationServices(Path.Combine(root, "roundtrip"), discoveryProviders: []);
                imported.Catalog.Import(app.Catalog.Export());
                Assert(imported.Catalog.Destinations().Single().Protection == DestinationProtection.PasswordFree, "Password-free protection was lost on export/import.");
            }
            finally { foreach (var name in names) Environment.SetEnvironmentVariable(name, original[name]); }
        });
        await tests.Run("Failed opens never register a repository or replace its valid key", async () =>
        {
            var destination = await app.Backups.AddDestinationAsync("Protected", Path.Combine(root, "protected"));
            var originalKey = app.Credentials.Get(destination.Id); var before = app.Catalog.Export();
            await Throws<IOException>(() => app.Backups.AddDestinationAsync("Bad key", destination.Path, "a-deliberately-invalid-recovery-key", openExisting: true));
            await Throws<InvalidOperationException>(() => app.Backups.AddDestinationAsync("Bad mode", destination.Path, openExisting: true, protection: DestinationProtection.PasswordFree));
            Assert(app.Catalog.Export() == before && app.Credentials.Get(destination.Id) == originalKey, "Failed reconnect changed registered credentials or metadata.");
            var fresh = new ApplicationServices(Path.Combine(root, "failed-open"), discoveryProviders: []);
            await Throws<IOException>(() => fresh.Backups.AddDestinationAsync("Bad key", destination.Path, "a-deliberately-invalid-recovery-key", openExisting: true));
            await Throws<IOException>(() => fresh.Backups.AddDestinationAsync("Bad mode", destination.Path, openExisting: true, protection: DestinationProtection.PasswordFree));
            Assert(fresh.Catalog.Destinations().Count == 0 && !Directory.EnumerateFiles(fresh.Paths.CredentialsDirectory).Any(), "Failed open left a destination or credential behind.");
        });
        await tests.Run("Recovery-key exports are reusable and preserve existing files on failure", async () =>
        {
            var protectedDestination = app.Catalog.Destinations().Single(d => d.Protection == DestinationProtection.RecoveryKey);
            var file = Path.Combine(root, "recovery.key"); await app.Backups.ExportRecoveryKeyAsync(protectedDestination, file);
            Assert(await File.ReadAllTextAsync(file) == app.Credentials.Get(protectedDestination.Id), "Exported key differs from saved credential.");
            await Throws<IOException>(() => app.Backups.ExportRecoveryKeyAsync(protectedDestination, file));
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Throws<OperationCanceledException>(() => app.Backups.ExportRecoveryKeyAsync(protectedDestination, file, overwrite: true, token: cancellation.Token));
            Assert(await File.ReadAllTextAsync(file) == app.Credentials.Get(protectedDestination.Id) && !Directory.EnumerateFiles(root, "*.tmp").Any(), "Failed export changed the key file or left temporary plaintext.");
            await Throws<InvalidOperationException>(() => app.Backups.ExportRecoveryKeyAsync(protectedDestination, Path.Combine(protectedDestination.Path, "config"), overwrite: true));
            var passwordFree = app.Catalog.Destinations().Single(d => d.Protection == DestinationProtection.PasswordFree);
            var unnecessary = Path.Combine(root, "unnecessary.key");
            await Throws<InvalidOperationException>(() => app.Backups.ExportRecoveryKeyAsync(passwordFree, unnecessary));
            Assert(!File.Exists(unnecessary), "Password-free key export wrote a file.");
        });
        await tests.Run("Conflicting or unsupported protection fails before creating a repository", async () =>
        {
            var target = Path.Combine(root, "invalid"); var before = app.Catalog.Export();
            await Throws<ArgumentException>(() => app.Backups.AddDestinationAsync("Invalid", target, "fixture-key-that-must-not-be-used", protection: DestinationProtection.PasswordFree));
            await Throws<ArgumentException>(() => app.Backups.AddDestinationAsync("Invalid", target, protection: (DestinationProtection)99));
            Assert(!Directory.Exists(target) && app.Catalog.Export() == before, "Invalid protection changed repository or catalog state.");
        });
    }
}
