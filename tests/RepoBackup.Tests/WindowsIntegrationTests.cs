using RepoBackup.Core.Application;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using RepoBackup.Core.Discovery;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class WindowsIntegrationTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        await tests.Run("Windows Task Scheduler runs the hidden backup runner with the GUI closed", async () =>
        {
            var runner = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "src", "RepoBackup.Runner", "bin", "Release", "net10.0-windows", "RepoBackup.Runner.exe"));
            var state = Path.Combine(root, "scheduled-codex.json"); await File.WriteAllTextAsync(state, "{\"local-projects\":{}}");
            var app = new ApplicationServices(Path.Combine(root, "scheduled-catalog"), schedulerRunnerPath: runner, codexStatePath: state);
            app.Catalog.SaveSetting("discoveryOptions", new DiscoveryOptions { Source = DiscoveryProviders.Codex, CodexStatePath = state });
            var source = Path.Combine(root, "scheduled-source"); Directory.CreateDirectory(source); await File.WriteAllTextAsync(Path.Combine(source, "fixture.txt"), "Headless scheduled backup fixture");
            app.Catalog.RegisterCandidate("Scheduler fixture", [source], "Fixture", approved: true);
            var destination = await app.Backups.AddDestinationAsync("Scheduler fixture repository", Path.Combine(root, "scheduled-repository"));
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
            }
            finally { if (installed) await app.Scheduler.DeleteAsync(schedule.Id); }
        });
    }
}
