using System.Text;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using RepoBackup.Core.Windows;

namespace RepoBackup.Core.Backup;

public sealed partial class BackupService
{
    public const string RecoveryKeyWarning = "Restoring on another computer requires your recovery key. If you lose all copies, your backups cannot be recovered. Export it and keep it separate from the backup drive.";

    // Keep the original API default for existing callers; the desktop and CLI explicitly select their default.
    public async Task<Destination> AddDestinationAsync(string name, string path, string? recoveryKey = null, bool openExisting = false,
        CancellationToken token = default, DestinationProtection protection = DestinationProtection.RecoveryKey)
    {
        if (!Enum.IsDefined(protection)) throw new ArgumentException("Unsupported destination protection mode.");
        if (protection == DestinationProtection.PasswordFree && recoveryKey is not null)
            throw new ArgumentException("A password-free repository cannot be supplied with a recovery key.");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Enter a destination name.");
        path = PathSafety.Normalize(path);
        PathSafety.EnsureDestinationOutsideSources(path, catalog.Projects().Where(p => p.Enabled && p.Reviewed).SelectMany(p => p.Roots).Select(r => r.Path));
        var existing = catalog.Destinations().FirstOrDefault(d => PathSafety.PhysicalPath(d.Path).Equals(PathSafety.PhysicalPath(path), StringComparison.OrdinalIgnoreCase));
        if (existing is not null && !openExisting) throw new InvalidOperationException("This destination is already registered. Use Open Existing to reconnect it.");
        if (existing is not null && existing.Protection != protection)
            throw new InvalidOperationException("Choose the registered destination's protection mode. Opening a repository does not change its protection.");
        if (!Directory.Exists(Path.GetPathRoot(path))) throw new IOException("The destination drive or network share is unavailable.");
        if (openExisting && !File.Exists(Path.Combine(path, "config"))) throw new IOException("Choose an existing restic repository containing its config file.");
        if (!openExisting && Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any()) throw new IOException("A new repository requires an empty folder. Use Open Existing for an initialized repository.");
        if (openExisting && protection == DestinationProtection.RecoveryKey && string.IsNullOrWhiteSpace(recoveryKey))
            throw new ArgumentException("The existing repository's recovery key is required.");
        if (existing is not null)
        {
            // Validate under a temporary identity before replacing any registered credential.
            var validation = existing with { Id = Guid.NewGuid().ToString("N"), Path = path };
            if (protection == DestinationProtection.RecoveryKey) credentials.Save(validation.Id, recoveryKey!);
            try
            {
                using var reconnectLease = DestinationLease.Acquire(paths, path);
                var repositoryId = await restic.RepositoryIdentityAsync(validation, token);
                var reconnected = existing with { Name = name.Trim(), Path = path, RepositoryId = repositoryId };
                if (protection == DestinationProtection.RecoveryKey) credentials.Save(existing.Id, recoveryKey!);
                catalog.SaveDestination(reconnected); return reconnected;
            }
            finally { if (protection == DestinationProtection.RecoveryKey) credentials.Delete(validation.Id); }
        }
        var destination = new Destination { Name = name.Trim(), Path = path, Protection = protection };
        if (protection == DestinationProtection.RecoveryKey) credentials.Save(destination.Id, recoveryKey ?? CredentialStore.Generate());
        try
        {
            Directory.CreateDirectory(path);
            using var lease = DestinationLease.Acquire(paths, path);
            if (!openExisting) (await restic.RunAsync(destination, ["init"], token)).EnsureSuccess("Initialize backup repository");
            destination = destination with { RepositoryId = await restic.RepositoryIdentityAsync(destination, token) };
            catalog.SaveDestination(destination); return destination;
        }
        catch
        {
            // Retain a newly initialized repository's key on cancellation, but never register a failed open.
            if (!openExisting && File.Exists(Path.Combine(path, "config"))) catalog.SaveDestination(destination);
            else if (protection == DestinationProtection.RecoveryKey) credentials.Delete(destination.Id);
            throw;
        }
    }

    public async Task ExportRecoveryKeyAsync(Destination destination, string file, bool overwrite = false, CancellationToken token = default)
    {
        if (destination.Protection != DestinationProtection.RecoveryKey)
            throw new InvalidOperationException("This destination requires no password and has no recovery key to export.");
        file = PathSafety.Normalize(file);
        if (PathSafety.IsWithin(PathSafety.PhysicalPath(file), PathSafety.PhysicalPath(destination.Path)))
            throw new InvalidOperationException("Save the recovery key outside the backup repository.");
        var key = credentials.Get(destination.Id);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, key, new UTF8Encoding(false), token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, file, overwrite);
        }
        finally { File.Delete(temporary); }
    }
}
