namespace RepoBackup.Core.Infrastructure;

/// <summary>Keeps setup/uninstall from replacing files during an app or runner operation.</summary>
public sealed class InstallationLifetime : IDisposable
{
    // Keep a handle open without taking ownership: app, CLI, and runner can coexist.
    // Inno Setup's AppMutex directive checks whether this named object exists.
    private readonly Mutex inUse = new(false, @"Local\RepoBackup.InUse");
    public void Dispose() => inUse.Dispose();
}
