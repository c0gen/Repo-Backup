using System.Diagnostics;
using RepoBackup.Core.Application;
using RepoBackup.Core.Backup;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class ResticLockRecoveryTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        await tests.Run("A newly stale native lock is recovered after lock acquisition fails", async () =>
        {
            var app = new ApplicationServices(Path.Combine(root, "stale-catalog"), discoveryProviders: []);
            var destination = await app.Backups.AddDestinationAsync("Stale lock", Path.Combine(root, "stale-repository"), protection: DestinationProtection.PasswordFree);
            using var holder = await HoldAsync(app, destination);
            // Retain the terminated Windows process handle to reproduce restic's OpenProcess-only liveness check.
            var runner = new LockRunner(async () => { holder.Kill(true); await holder.WaitForExitAsync(); });
            var client = new ResticClient(app.Paths, app.Credentials, runner, app.Restic.Executable);
            (await client.RunAsync(destination, ["check"])).EnsureSuccess("Recover newly stale lock");
            Assert(runner.Failures == 1 && runner.Unlocks >= 1 && !Directory.EnumerateFiles(Path.Combine(destination.Path, "locks")).Any(),
                "The newly stale lock was not safely recovered.");
        });
        await tests.Run("Lock recovery preserves a live owner and remains cancellable", async () =>
        {
            var app = new ApplicationServices(Path.Combine(root, "active-catalog"), discoveryProviders: []);
            var destination = await app.Backups.AddDestinationAsync("Active lock", Path.Combine(root, "active-repository"), protection: DestinationProtection.PasswordFree);
            using var holder = await HoldAsync(app, destination);
            try
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await Throws<OperationCanceledException>(() => app.Restic.RunAsync(destination, ["check"], cancellation.Token));
                Assert(!holder.HasExited && Directory.EnumerateFiles(Path.Combine(destination.Path, "locks")).Any(), "Recovery removed an active owner's lock.");
                holder.StandardInput.Close(); await holder.WaitForExitAsync();
                Assert(holder.ExitCode == 0, "Native lock holder failed.");
                (await app.Restic.RunAsync(destination, ["check"])).EnsureSuccess("Verify after live owner exits");
            }
            finally { if (!holder.HasExited) { holder.Kill(true); await holder.WaitForExitAsync(); } }
        });
        await tests.Run("Streamed snapshot listing resets failed parse state after lock recovery", async () =>
        {
            var app = new ApplicationServices(Path.Combine(root, "stream-catalog"), discoveryProviders: []);
            var destination = await app.Backups.AddDestinationAsync("Stream lock", Path.Combine(root, "stream-repository"), protection: DestinationProtection.PasswordFree);
            var runner = new StreamLockRunner();
            var client = new ResticClient(app.Paths, app.Credentials, runner, app.Restic.Executable);
            Assert((await client.SnapshotSummariesAsync(destination)).Count == 0 && runner.Attempts == 2,
                "A failed lock attempt's empty output contaminated the successful streamed listing.");
        });
        await tests.Run("Lock recovery validates repository identity before modifying lock metadata", async () =>
        {
            var app = new ApplicationServices(Path.Combine(root, "identity-catalog"), discoveryProviders: []);
            var destination = await app.Backups.AddDestinationAsync("Identity lock", Path.Combine(root, "identity-repository"), protection: DestinationProtection.PasswordFree);
            using var holder = await HoldAsync(app, destination);
            try
            {
                await Throws<InvalidDataException>(() => app.Restic.RunAsync(destination with { RepositoryId = "substituted" }, ["check"]));
                Assert(!holder.HasExited && Directory.EnumerateFiles(Path.Combine(destination.Path, "locks")).Any(), "Lock recovery touched a substituted repository.");
                holder.StandardInput.Close(); await holder.WaitForExitAsync();
            }
            finally { if (!holder.HasExited) { holder.Kill(true); await holder.WaitForExitAsync(); } }
        });
    }

    private static async Task<Process> HoldAsync(ApplicationServices app, Destination destination)
    {
        var start = new ProcessStartInfo(app.Restic.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "--repo", destination.Path, "--json", "--insecure-no-password", "backup", "--stdin", "--stdin-filename", "held-input" }) start.ArgumentList.Add(arg);
        start.Environment["RESTIC_PASSWORD"] = "";
        foreach (var name in new[] { "RESTIC_PASSWORD_FILE", "RESTIC_PASSWORD_COMMAND", "RESTIC_KEY_HINT", "RESTIC_REPOSITORY_FILE" }) start.Environment.Remove(name);
        start.Environment["RESTIC_CACHE_DIR"] = app.Paths.CacheDirectory;
        var process = Process.Start(start) ?? throw new IOException("Cannot start native lock fixture.");
        _ = process.StandardOutput.ReadToEndAsync(); _ = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteAsync("held fixture input"); await process.StandardInput.FlushAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!Directory.EnumerateFiles(Path.Combine(destination.Path, "locks")).Any())
            {
                if (process.HasExited) throw new IOException("Native lock holder exited before creating its lock.");
                await Task.Delay(50, timeout.Token);
            }
            // Allow the native lock's second acquisition check to finish before deliberately conflicting.
            await Task.Delay(250, timeout.Token);
            return process;
        }
        catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); process.Dispose(); throw; }
    }

    private sealed class LockRunner(Func<Task> release) : ProcessRunner
    {
        public int Failures { get; private set; }
        public int Unlocks { get; private set; }
        public override async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken token = default,
            string? workingDirectory = null, IReadOnlyDictionary<string, string?>? environment = null, Action<string>? onOutput = null, string? secret = null, bool captureOutput = true)
        {
            var args = arguments.ToArray();
            Assert(!args.Contains("--remove-all"), "Unsafe removal of live locks was requested.");
            if (args.Contains("unlock")) Unlocks++;
            var result = await base.RunAsync(executable, args, token, workingDirectory, environment, onOutput, secret, captureOutput);
            if (args.Contains("check") && result.ExitCode == 11 && Failures++ == 0) await release();
            return result;
        }
    }

    private sealed class StreamLockRunner : ProcessRunner
    {
        public int Attempts { get; private set; }
        public override async Task<ProcessResult> RunStreamAsync(string executable, IEnumerable<string> arguments,
            Func<Stream, CancellationToken, Task> consume, CancellationToken token = default,
            IReadOnlyDictionary<string, string?>? environment = null, string? secret = null)
        {
            if (++Attempts == 1)
            {
                using var empty = new MemoryStream(); await consume(empty, token);
                return new(11, "", "Injected transient native lock.");
            }
            return await base.RunStreamAsync(executable, arguments, consume, token, environment, secret);
        }
    }
}
