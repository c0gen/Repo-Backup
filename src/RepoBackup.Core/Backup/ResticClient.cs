using System.Security.Cryptography;
using System.Text.Json;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using RepoBackup.Core.Windows;

namespace RepoBackup.Core.Backup;

public sealed class ResticClient
{
    public const string Version = "0.19.1";
    public const string ExecutableSha256 = "B0DD1FD21EEA5D8FE1325F55F7118213C21F36DE8A261E04C0624A5AB9FD7830";
    private readonly AppPaths paths;
    private readonly CredentialStore credentials;
    private readonly ProcessRunner processes;
    public string Executable { get; }
    public ResticClient(AppPaths paths, CredentialStore credentials, ProcessRunner processes, string? executable = null)
    { this.paths = paths; this.credentials = credentials; this.processes = processes; Executable = executable ?? Path.Combine(AppContext.BaseDirectory, "restic", "restic.exe"); }

    private async Task VerifyExecutableAsync(CancellationToken token)
    {
        await using var stream = File.OpenRead(Executable); var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
        if (hash != ExecutableSha256) throw new InvalidDataException("Restic executable checksum does not match the pinned release. Reinstall Repo Backup.");
    }
    public async Task<ProcessResult> RunAsync(Destination destination, IEnumerable<string> args, CancellationToken token = default, Action<string>? output = null)
    {
        await VerifyExecutableAsync(token);
        var password = destination.Protection switch
        {
            DestinationProtection.RecoveryKey => credentials.Get(destination.Id),
            DestinationProtection.PasswordFree => "",
            _ => throw new ArgumentException("Unsupported destination protection mode.")
        };
        var environment = new Dictionary<string, string> { ["RESTIC_PASSWORD"] = password, ["RESTIC_CACHE_DIR"] = paths.CacheDirectory, ["RESTIC_PASSWORD_FILE"] = "", ["RESTIC_PASSWORD_COMMAND"] = "", ["RESTIC_KEY_HINT"] = "", ["RESTIC_REPOSITORY_FILE"] = "" };
        var arguments = new List<string> { "--repo", destination.Path, "--json" };
        if (destination.Protection == DestinationProtection.PasswordFree) arguments.Add("--insecure-no-password");
        return await processes.RunAsync(Executable, arguments.Concat(args), token, environment: environment, onOutput: output, secret: password);
    }
    public async Task<string> RepositoryIdentityAsync(Destination destination, CancellationToken token = default)
    {
        var result = await RunAsync(destination, ["cat", "config"], token);
        if (destination.Protection == DestinationProtection.PasswordFree && result.ExitCode == 12)
            throw new IOException("This repository requires a recovery key. Choose recovery-key protection and load its key file.");
        result.EnsureSuccess("Read repository identity");
        using var json = JsonDocument.Parse(result.Output); var id = json.RootElement.GetProperty("id").GetString()!;
        if (destination.RepositoryId is not null && destination.RepositoryId != id) throw new InvalidDataException("The destination contains a different backup repository. Select the correct drive or explicitly open the replacement repository.");
        return id;
    }
    public async Task PrepareRepositoryAsync(Destination destination, CancellationToken token = default)
    {
        await RepositoryIdentityAsync(destination, token);
        // Restic's ordinary unlock removes only stale locks; never use --remove-all.
        (await RunAsync(destination, ["unlock"], token)).EnsureSuccess("Recover stale repository locks");
    }
    public async Task<List<SnapshotInfo>> SnapshotsAsync(Destination destination, CancellationToken token = default)
    {
        var result = await RunAsync(destination, ["snapshots", "--tag", "repobackup"], token); result.EnsureSuccess("List snapshots");
        using var json = JsonDocument.Parse(result.Output);
        if (json.RootElement.ValueKind == JsonValueKind.Null) return [];
        return json.RootElement.EnumerateArray().Select(s => new SnapshotInfo(s.GetProperty("id").GetString()!, s.GetProperty("time").GetDateTimeOffset(), s.GetProperty("tags").EnumerateArray().Select(t => t.GetString()!).ToArray(), s.GetProperty("paths").EnumerateArray().Select(t => t.GetString()!).ToArray())).OrderByDescending(s => s.Time).ToList();
    }
    public async Task<RecoveryManifest> ManifestAsync(Destination destination, SnapshotInfo snapshot, CancellationToken token = default)
    {
        var encoded = snapshot.TagValue("manifest") ?? throw new InvalidDataException("Snapshot has no recovery manifest.");
        var path = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        var result = await RunAsync(destination, ["dump", snapshot.Id, path], token); result.EnsureSuccess("Read recovery manifest");
        var manifest = Json.Read<RecoveryManifest>(result.Output);
        if (manifest.SchemaVersion != 1 || manifest.SeriesId != snapshot.TagValue("series") || manifest.JobId != snapshot.TagValue("run")) throw new InvalidDataException("Snapshot recovery manifest identity or version is invalid.");
        return manifest;
    }
    public async Task<List<SnapshotFile>> FilesAsync(Destination destination, string snapshotId, CancellationToken token = default)
    {
        var result = await RunAsync(destination, ["ls", snapshotId], token); result.EnsureSuccess("Browse snapshot");
        var files = new List<SnapshotFile>();
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var json = JsonDocument.Parse(line); var item = json.RootElement;
            if (item.TryGetProperty("struct_type", out var type) && type.GetString() == "node")
                files.Add(new(item.GetProperty("path").GetString()!, item.GetProperty("type").GetString()!, item.TryGetProperty("size", out var size) ? size.GetInt64() : 0));
        }
        return files;
    }
}
