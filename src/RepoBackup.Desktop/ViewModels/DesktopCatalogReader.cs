using RepoBackup.Core.Application;
using RepoBackup.Core.Discovery;
using RepoBackup.Core.Models;
using RepoBackup.Core.Windows;

namespace RepoBackup.Desktop.ViewModels;

public sealed record DesktopCatalogSnapshot(
    List<ProjectEntry> Projects, List<Destination> Destinations, List<SavedSelection> Selections,
    List<JobRecord> History, List<VerificationRecord> Verifications, HashSet<string> AvailableProjects,
    string? SelectedDestinationId, bool UseVss, string CodexHook, string ClaudeHook, string AntigravityHook);

public sealed record DesktopScheduleSnapshot(List<ScheduleDefinition> Schedules, List<Destination> Destinations, List<SavedSelection> Selections);

public static class DesktopCatalogReader
{
    public static Task<DesktopCatalogSnapshot> ReadAsync(ApplicationServices services, CancellationToken token) => Task.Run(() =>
    {
        var catalog = services.Catalog;
        var projects = catalog.Projects();
        var available = new HashSet<string>();
        foreach (var project in projects)
        {
            token.ThrowIfCancellationRequested();
            if (project.Roots.All(r => Directory.Exists(r.Path))) available.Add(project.Id);
        }
        return new DesktopCatalogSnapshot(projects, catalog.Destinations(), catalog.Selections(), catalog.History(),
            catalog.GetSetting<List<VerificationRecord>>("verificationHistory") ?? [], available,
            catalog.GetSetting<string>("selectedDestination"), catalog.GetSetting<bool>("useVss"),
            DiscoveryHookInstaller.Status(catalog, DiscoveryProviders.Codex),
            DiscoveryHookInstaller.Status(catalog, DiscoveryProviders.ClaudeCode),
            DiscoveryHookInstaller.Status(catalog, DiscoveryProviders.Antigravity));
    }, token);

    public static Task<DesktopScheduleSnapshot> ReadSchedulesAsync(ApplicationServices services, CancellationToken token) => Task.Run(() =>
        new DesktopScheduleSnapshot(services.Catalog.Schedules(), services.Catalog.Destinations(), services.Catalog.Selections()), token);
}
