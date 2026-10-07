using RepoBackup.Core.Backup;
using RepoBackup.Core.Git;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using RepoBackup.Core.Recovery;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class RestoreServiceProgressTests
{
    public static async Task RunAsync(TestRunner tests, Fixture fixture)
    {
        var app = fixture.App;
        var selected = PathSafety.SnapshotPath(Path.Combine(fixture.Main, ".env"));
        await tests.Run("Restore summary followed by verification failure cannot record success", async () =>
        {
            var before = app.Catalog.GetSetting<System.Text.Json.JsonElement>("lastRestore").ToString();
            var process = new VerificationFailureRunner();
            var client = new ResticClient(app.Paths, app.Credentials, process, app.Restic.Executable);
            var service = new RestoreService(app.Paths, app.Catalog, client, new GitRecovery(new GitInspector(new ProcessRunner())));
            var progress = new ProgressRecorder(); var target = Path.Combine(fixture.Root, "verification-failure");
            await Throws<IOException>(() => service.RestoreAsync(fixture.Destination, fixture.Snapshot, target, selected, progress));
            Assert(process.Streamed && progress.Items.Any(p => p.IsIndeterminate && p.Stage.StartsWith("Verifying "))
                && progress.Items.All(p => p.Fraction < 1) && File.Exists(Path.Combine(target, "RESTORE-INCOMPLETE.txt"))
                && app.Catalog.GetSetting<System.Text.Json.JsonElement>("lastRestore").ToString() == before, "Verification failure claimed successful completion.");
        });
        await tests.Run("Cancelling real restore verification leaves progress incomplete and releases the lease", async () =>
        {
            var before = app.Catalog.GetSetting<System.Text.Json.JsonElement>("lastRestore").ToString();
            using var cancelled = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var progress = new ProgressRecorder(p => { if (p.Stage.StartsWith("Verifying ")) cancelled.Cancel(); });
            var target = Path.Combine(fixture.Root, "verification-cancelled");
            await Throws<OperationCanceledException>(() => app.Restore.RestoreAsync(fixture.Destination, fixture.Snapshot, target, selected, progress, cancelled.Token));
            Assert(progress.Items.Any(p => p.Stage.StartsWith("Verifying ")) && progress.Items.All(p => p.Fraction < 1)
                && File.Exists(Path.Combine(target, "RESTORE-INCOMPLETE.txt"))
                && app.Catalog.GetSetting<System.Text.Json.JsonElement>("lastRestore").ToString() == before, "Cancelled verification claimed success.");
            using var lease = DestinationLease.Acquire(app.Paths, fixture.Destination.Path);
        });
    }

    private sealed class VerificationFailureRunner : ProcessRunner
    {
        public bool Streamed { get; private set; }
        public override Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken token = default,
            string? workingDirectory = null, IReadOnlyDictionary<string, string>? environment = null, Action<string>? onOutput = null, string? secret = null, bool captureOutput = true)
        {
            var args = arguments.ToList();
            if (!args.Contains("restore")) return base.RunAsync(executable, args, token, workingDirectory, environment, onOutput, secret, captureOutput);
            Streamed = !captureOutput && args.Contains("--verbose=2") && args.Contains("--verify") && onOutput is not null;
            onOutput?.Invoke(RestoreProgressTests.Summary(1, 1));
            return Task.FromResult(new ProcessResult(1, "", "Injected verification failure after restore summary."));
        }
    }
}
