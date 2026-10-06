using RepoBackup.Core.Models;

namespace RepoBackup.Desktop.Infrastructure;

public sealed record DestinationInput(string Name, string Path, string? RecoveryKey, bool OpenExisting, DestinationProtection Protection);
