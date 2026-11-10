using RepoBackup.Core.Application;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using RepoBackup.Core.Discovery;
using System.Xml.Linq;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class WindowsIntegrationTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        foreach (var protection in Enum.GetValues<DestinationProtection>())
        await tests.Run("Windows Task Scheduler runs the hidden backup runner with the GUI closed: " + protection, async () =>
        {
            var fixtureRoot = Path.Combine(root, "scheduler-" + protection); Directory.CreateDirectory(fixtureRoot);
            var runner = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "src", "RepoBackup.Runner", "bin", "Release", "net10.0-windows", "RepoBackup.Runner.exe"));
            var state = Path.Combine(fixtureRoot, "scheduled-codex.json"); await File.WriteAllTextAsync(state, "{\"local-projects\":{}}");
            var app = new ApplicationServices(Path.Combine(fixtureRoot, "scheduled catalog 项目") + Path.DirectorySeparatorChar, schedulerRunnerPath: runner, codexStatePath: state);
            app.Catalog.SaveSetting("discoveryOptions", new DiscoveryOptions { Source = DiscoveryProviders.Codex, CodexStatePath = state });
            var source = Path.Combine(fixtureRoot, "scheduled-source"); Directory.CreateDirectory(source); await File.WriteAllTextAsync(Path.Combine(source, "fixture.txt"), "Headless scheduled backup fixture");
            app.Catalog.RegisterCandidate("Scheduler fixture", [source], "Fixture", approved: true);
            var destination = await app.Backups.AddDestinationAsync("Scheduler fixture repository", Path.Combine(fixtureRoot, "scheduled-repository"), protection: protection);
            var schedule = new ScheduleDefinition { DestinationId = destination.Id, Name = "Isolated acceptance fixture", Enabled = true, Kind = ScheduleKind.Daily };
            var installed = false;
            try
            {
                await app.Scheduler.SaveAsync(schedule); installed = true;
                (await new ProcessRunner().RunAsync("schtasks.exe", ["/Run", "/TN", "RepoBackup-" + schedule.Id])).EnsureSuccess("Run isolated scheduled task");
                var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
                JobRecord? completed = null;
                while (DateTimeOffset.UtcNow < deadline)
                {
                    completed = app.Catalog.History().FirstOrDefault(j => j.FinishedAt is not null);
                    if (completed is not null) break;
                    await Task.Delay(200);
                }
                Assert(completed?.Backup == Outcome.Successful && completed.Verification == Outcome.Successful, completed is null ? "Scheduled runner did not finish within 60 seconds." : Json.Write(completed));
                Assert((await app.Restic.SnapshotsAsync(destination)).Single().Successful, "Scheduled snapshot was not successful.");
                // Wait for the fixture runner to release its repository before replacing it.
                var released = false;
                for (var attempt = 0; attempt < 50 && !released; attempt++)
                {
                    try { using var lease = RepoBackup.Core.Backup.DestinationLease.Acquire(app.Paths, destination.Path); released = true; }
                    catch (IOException) { await Task.Delay(100); }
                }
                Assert(released, "Scheduled runner retained its destination lease.");
                var previousPath = Path.Combine(fixtureRoot, "previous-repository");
                Assert(PathSafety.IsWithin(destination.Path, fixtureRoot) && PathSafety.IsWithin(previousPath, fixtureRoot), "Unsafe scheduled fixture move.");
                Directory.Move(destination.Path, previousPath);
                var other = new ApplicationServices(Path.Combine(fixtureRoot, "replacement-catalog"), discoveryProviders: []);
                var repository = await other.Backups.AddDestinationAsync("Replacement", destination.Path, protection: protection);
                var replacement = await app.Backups.AddDestinationAsync("Replacement", destination.Path,
                    protection == DestinationProtection.RecoveryKey ? other.Credentials.Get(repository.Id) : null, true, protection: protection);
                var query = await new ProcessRunner().RunAsync("schtasks.exe", ["/Query", "/TN", "RepoBackup-" + schedule.Id, "/XML"]); query.EnsureSuccess("Inspect disabled fixture task");
                XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
                Assert(XDocument.Parse(query.Output).Root!.Element(ns + "Settings")!.Element(ns + "Enabled")!.Value == "false" &&
                    !app.Catalog.Schedules().Single().Enabled && replacement.Id != destination.Id, "Replacement left an actual Windows schedule enabled.");
            }
            finally { if (installed) await app.Scheduler.DeleteAsync(schedule.Id); }
        });
    }
}
