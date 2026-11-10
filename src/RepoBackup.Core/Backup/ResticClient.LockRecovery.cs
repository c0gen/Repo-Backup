using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Backup;

public sealed partial class ResticClient
{
    private async Task<ProcessResult> WithLockRecoveryAsync(Destination destination, Func<Task<ProcessResult>> execute,
        IReadOnlyDictionary<string, string?> environment, string secret, CancellationToken token, bool recover = true)
    {
        var result = await execute();
        // Exit 11 means lock acquisition failed before the command started. A killed command
        // can leave a lock which was not yet removable during the initial preparation.
        // Restic's lock acquisition does not discard stale locks itself: use ordinary unlock only.
        for (var attempt = 0; recover && result.ExitCode == 11 && attempt < 5; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250 * (1 << attempt)), token);
            await RepositoryIdentityAsync(destination, token);
            var arguments = new List<string> { "--repo", destination.Path, "--json" };
            if (destination.Protection == DestinationProtection.PasswordFree) arguments.Add("--insecure-no-password");
            arguments.Add("unlock");
            var unlocked = await processes.RunAsync(Executable, arguments, token, environment: environment, secret: secret);
            if (unlocked.ExitCode != 0) return result;
            await ResticLockFiles.RemoveExitedOwnersAsync(destination, command => processes.RunAsync(Executable,
                arguments.Take(arguments.Count - 1).Concat(new[] { "--no-lock" }).Concat(command), token, environment: environment, secret: secret), token);
            result = await execute();
        }
        return result;
    }
}
