using System.Text.Json;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Backup;

public sealed partial class ResticClient
{
    public async Task<List<SnapshotFile>> FilesAsync(Destination destination, string snapshotId, CancellationToken token = default)
    {
        if (destination.IsRetired) throw new InvalidOperationException("Choose an active destination.");
        await VerifyExecutableAsync(token);
        var password = destination.Protection == DestinationProtection.PasswordFree ? "" : credentials.Get(destination.Id);
        var environment = new Dictionary<string, string?> { ["RESTIC_PASSWORD"] = password, ["RESTIC_CACHE_DIR"] = paths.CacheDirectory,
            ["RESTIC_PASSWORD_FILE"] = "", ["RESTIC_PASSWORD_COMMAND"] = "", ["RESTIC_KEY_HINT"] = "", ["RESTIC_REPOSITORY_FILE"] = "" };
        var args = new List<string> { "--repo", destination.Path, "--json" };
        if (destination.Protection == DestinationProtection.PasswordFree) args.Add("--insecure-no-password");
        args.AddRange(["ls", snapshotId]);
        List<SnapshotFile> files = [];
        var result = await WithLockRecoveryAsync(destination, () =>
        {
            files = []; // A stale-lock retry owns a new result, never a duplicate prefix.
            return processes.RunStreamAsync(Executable, args, async (stream, cancellation) =>
            {
                using var reader = new StreamReader(stream, leaveOpen: true);
                while (await reader.ReadLineAsync(cancellation) is { } line)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    using var json = JsonDocument.Parse(line); var item = json.RootElement;
                    if (item.TryGetProperty("struct_type", out var type) && type.GetString() == "node")
                        files.Add(new(item.GetProperty("path").GetString()!, item.GetProperty("type").GetString()!,
                            item.TryGetProperty("size", out var size) ? size.GetInt64() : 0));
                }
            }, token, environment, password);
        }, environment, password, token);
        result.EnsureSuccess("Browse snapshot");
        return files;
    }
}
