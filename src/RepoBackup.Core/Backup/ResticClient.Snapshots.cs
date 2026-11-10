using System.Text.Json;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Backup;

public sealed partial class ResticClient
{
    public async Task<List<SnapshotSummary>> SnapshotSummariesAsync(Destination destination, CancellationToken token = default)
    {
        if (destination.IsRetired) throw new InvalidOperationException("This destination was replaced. Choose an active destination.");
        await VerifyExecutableAsync(token);
        var password = destination.Protection switch
        {
            DestinationProtection.RecoveryKey => credentials.Get(destination.Id), DestinationProtection.PasswordFree => "",
            _ => throw new ArgumentException("Unsupported destination protection mode.")
        };
        var environment = new Dictionary<string, string?> { ["RESTIC_PASSWORD"] = password, ["RESTIC_CACHE_DIR"] = paths.CacheDirectory,
            ["RESTIC_PASSWORD_FILE"] = "", ["RESTIC_PASSWORD_COMMAND"] = "", ["RESTIC_KEY_HINT"] = "", ["RESTIC_REPOSITORY_FILE"] = "" };
        var args = new List<string> { "--repo", destination.Path, "--json" };
        if (destination.Protection == DestinationProtection.PasswordFree) args.Add("--insecure-no-password");
        args.AddRange(["snapshots", "--tag", "repobackup"]);
        List<SnapshotSummary> snapshots = []; JsonException? parseError = null;
        var result = await WithLockRecoveryAsync(destination, () =>
            {
                parseError = null; snapshots = [];
                return processes.RunStreamAsync(Executable, args, async (stream, cancellation) =>
                {
                    try { snapshots = await SnapshotSummaryReader.ReadAsync(stream, cancellation); }
                    catch (JsonException error) { parseError = error; }
                }, token, environment, password);
            }, environment, password, token);
        result.EnsureSuccess("List snapshot summaries");
        if (parseError is not null) throw parseError;
        return snapshots;
    }
}
