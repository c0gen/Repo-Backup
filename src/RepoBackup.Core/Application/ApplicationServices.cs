using RepoBackup.Core.Backup;
using RepoBackup.Core.Discovery;
using RepoBackup.Core.Git;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Recovery;
using RepoBackup.Core.Storage;
using RepoBackup.Core.Windows;

namespace RepoBackup.Core.Application;

public sealed class ApplicationServices
{
    public AppPaths Paths { get; }
    public CatalogStore Catalog { get; }
    public CredentialStore Credentials { get; }
    public ProjectDiscoveryService Discovery { get; }
    public SelectionPlanner Planner { get; }
    public ResticClient Restic { get; }
    public BackupService Backups { get; }
    public RestoreService Restore { get; }
    public WindowsScheduler Scheduler { get; }
    public ApplicationServices(string? dataDirectory = null, string? resticExecutable = null, string? codexStatePath = null, string? schedulerRunnerPath = null,
        IEnumerable<IProjectDiscoveryProvider>? discoveryProviders = null, DiscoveryOptions? discoveryOptions = null)
    {
        Paths = new(dataDirectory); Catalog = new(Paths); Credentials = new(Paths);
        var processes = new ProcessRunner(); var git = new GitInspector(processes);
        var providers = discoveryProviders ?? ProjectDiscoveryService.CreateProviders(discoveryOptions ?? (codexStatePath is null
            ? Catalog.GetSetting<DiscoveryOptions>("discoveryOptions") ?? new DiscoveryOptions()
            : new DiscoveryOptions { Source = DiscoveryProviders.Codex, CodexStatePath = codexStatePath }));
        Discovery = new(Catalog, providers); Planner = new(git); Restic = new(Paths, Credentials, processes, resticExecutable);
        Backups = new(Paths, Catalog, Credentials, Restic, Planner, Discovery);
        Restore = new(Paths, Catalog, Restic, new GitRecovery(git)); Scheduler = new(Paths, Catalog, processes, schedulerRunnerPath);
    }
}
