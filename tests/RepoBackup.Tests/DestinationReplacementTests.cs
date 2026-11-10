using RepoBackup.Cli;
using RepoBackup.Core.Application;
using RepoBackup.Core.Backup;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class DestinationReplacementTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        await tests.Run("A permission failure releases destination handles for immediate retry", () =>
        {
            var paths = new AppPaths(Path.Combine(root, "lease-catalog")); var repository = Path.Combine(root, "lease-repository"); Directory.CreateDirectory(repository);
            var file = Path.Combine(repository, ".repobackup-operation.lock"); File.WriteAllText(file, ""); File.SetAttributes(file, FileAttributes.ReadOnly);
            try { using var unexpected = DestinationLease.Acquire(paths, repository); throw new InvalidOperationException("Read-only lock unexpectedly opened."); }
            catch (UnauthorizedAccessException) { }
            finally { File.SetAttributes(file, FileAttributes.Normal); }
            using var retry = DestinationLease.Acquire(paths, repository); return Task.CompletedTask;
        });
        foreach (var protection in Enum.GetValues<DestinationProtection>())
        await tests.Run("Explicit replacement separates identity, protection history and schedules: " + protection, async () =>
        {
            var fixture = Path.Combine(root, protection.ToString()); var app = new ApplicationServices(Path.Combine(fixture, "catalog"), discoveryProviders: []);
            var path = Path.Combine(fixture, "repository");
            var original = await app.Backups.AddDestinationAsync("Original", path, protection: protection);
            var key = protection == DestinationProtection.RecoveryKey ? app.Credentials.Get(original.Id) : null;
            var reconnect = await app.Backups.AddDestinationAsync("Reconnected", path, key, true, protection: protection);
            Assert(reconnect.Id == original.Id, "Same repository lost its identity."); original = reconnect;
            var history = new JobRecord { ProjectId = "fixture", ProjectName = "Fixture", DestinationId = original.Id, SeriesId = "fixture", Backup = Outcome.Successful, Verification = Outcome.Successful, FinishedAt = DateTimeOffset.UtcNow };
            app.Catalog.SaveJob(history); var schedule = new ScheduleDefinition { DestinationId = original.Id, Enabled = true }; app.Catalog.SaveSchedules([schedule]);
            var previousPath = Path.Combine(fixture, "previous-repository");
            Assert(PathSafety.IsWithin(path, fixture) && PathSafety.IsWithin(previousPath, fixture), "Unsafe fixture move."); Directory.Move(path, previousPath);
            var other = new ApplicationServices(Path.Combine(fixture, "other-catalog"), discoveryProviders: []);
            var replacementProtection = protection == DestinationProtection.PasswordFree ? DestinationProtection.RecoveryKey : DestinationProtection.PasswordFree;
            var next = await other.Backups.AddDestinationAsync("Replacement", path, protection: replacementProtection);
            var nextKey = replacementProtection == DestinationProtection.RecoveryKey ? other.Credentials.Get(next.Id) : null;
            var before = app.Catalog.Export();
            await Throws<IOException>(() => app.Backups.AddDestinationAsync("Wrong key", path, new string('x', 32), true, protection: DestinationProtection.RecoveryKey));
            Assert(before == app.Catalog.Export() && (key is null || app.Credentials.Get(original.Id) == key), "Failed open changed previous state.");
            var replacement = await app.Backups.AddDestinationAsync("Replacement", path, nextKey, true, protection: replacementProtection);
            Assert(replacement.Id != original.Id && replacement.RepositoryId == next.RepositoryId && replacement.Protection == replacementProtection, "Replacement reused old identity or protection.");
            var retired = app.Catalog.Destinations().Single(d => d.Id == original.Id);
            Assert(retired.IsRetired && !app.Catalog.Schedules().Single().Enabled && Json.Write(app.Catalog.History().Single()) == Json.Write(history), "Replacement merged history or left schedules enabled.");
            Assert(key is null || app.Credentials.Get(original.Id) == key, "Retirement deleted the original recovery key.");
            await Throws<InvalidOperationException>(() => app.Restic.SnapshotsAsync(retired));
            using var errors = new StringWriter();
            Assert(await CommandHost.RunAsync(["--data-dir", app.Paths.DataDirectory, "run-schedule", "--id", schedule.Id], TextWriter.Null, errors) == 0 && app.Catalog.History().Count == 1, "Retired schedule started a backup.");
            await Throws<ArgumentException>(() => app.Scheduler.SaveAsync(schedule with { Enabled = true }));
            var same = await app.Backups.AddDestinationAsync("Again", path, nextKey, true, protection: replacementProtection);
            Assert(same.Id == replacement.Id && app.Catalog.Destinations().Count == 2, "Repeated opening duplicated the replacement.");
        });
    }
}
