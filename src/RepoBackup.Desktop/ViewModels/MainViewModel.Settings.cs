using RepoBackup.Core.Models;
using RepoBackup.Core.Windows;
using RepoBackup.Core.Discovery;

namespace RepoBackup.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private bool useVss;
    public bool UseVss { get => useVss; set { if (Set(ref useVss, value)) Services.Catalog.SaveSetting("useVss", value); } }
    public ScheduleEditorState ScheduleEditor { get; } = new();
    public System.Windows.Input.ICommand NewScheduleCommand { get; private set; } = null!;
    private ScheduleDefinition? selectedSchedule;
    public ScheduleDefinition? SelectedSchedule
    {
        get => selectedSchedule;
        set
        {
            if (!Set(ref selectedSchedule, value) || value is null) return;
            ScheduleEditor.Load(value);
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }
    public string DataPath => Services.Paths.DataDirectory;
    public string EngineVersion => "restic " + Core.Backup.ResticClient.Version + " · checksum verified at every operation";
    public string CodexHookStatus => DiscoveryHookInstaller.Status(Services.Catalog, DiscoveryProviders.Codex);
    public string ClaudeHookStatus => DiscoveryHookInstaller.Status(Services.Catalog, DiscoveryProviders.ClaudeCode);
    public string AntigravityHookStatus => DiscoveryHookInstaller.Status(Services.Catalog, DiscoveryProviders.Antigravity);

    private void RaiseHookStatus()
    {
        Raise(nameof(CodexHookStatus)); Raise(nameof(ClaudeHookStatus)); Raise(nameof(AntigravityHookStatus));
    }

    private Task InstallDiscoveryHookAsync(string provider)
    {
        DiscoveryHookInstaller.Install(provider, Services.Catalog, Services.Paths.DataDirectory);
        RaiseHookStatus(); StatusMessage = DiscoveryProviders.DisplayName(provider) + " hook configured; awaiting verification.";
        dialogs.Information("The hook records review candidates. Existing configuration was preserved. " + DiscoveryHookInstaller.Instructions(provider));
        return Task.CompletedTask;
    }
    private void InitializeSettingsCommands()
    {
        useVss = Services.Catalog.GetSetting<bool>("useVss");
        ExportCatalogCommand = Command(async () => { var path = dialogs.Save("Export catalog — credentials are excluded", "RepoBackup-catalog.json", "JSON|*.json"); if (path is not null) { await File.WriteAllTextAsync(path, Services.Catalog.Export()); StatusMessage = "Catalog exported without credentials. Schedules import disabled."; } });
        ImportCatalogCommand = Command(async () => { var path = dialogs.Open("Import a Repo Backup catalog", "JSON|*.json"); if (path is not null) { Services.Catalog.Import(await File.ReadAllTextAsync(path)); Reload(); StatusMessage = "Catalog imported. Recovery keys must be supplied separately."; } });
        InstallHookCommand = Command(() => InstallDiscoveryHookAsync(DiscoveryProviders.Codex));
        InstallClaudeHookCommand = Command(() => InstallDiscoveryHookAsync(DiscoveryProviders.ClaudeCode));
        InstallAntigravityHookCommand = Command(() => InstallDiscoveryHookAsync(DiscoveryProviders.Antigravity));
        NewScheduleCommand = Command(() => { SelectedSchedule = null; ScheduleEditor.New(SelectedDestination?.Id); return Task.CompletedTask; });
        SaveScheduleCommand = Command(async () =>
        {
            var schedule = ScheduleEditor.Build();
            await OperateAsync(async token =>
            {
                await Services.Scheduler.SaveAsync(schedule, token); ScheduleEditor.Saved(schedule);
                StatusMessage = schedule.Enabled ? "Schedule saved. Backups can run while the GUI is closed." : "Schedule saved disabled.";
            });
        }, () => ScheduleEditor.DestinationId is not null);
        DeleteScheduleCommand = Command(async () =>
        {
            var id = SelectedSchedule!.Id;
            await OperateAsync(async token => { await Services.Scheduler.DeleteAsync(id, token); SelectedSchedule = null; ScheduleEditor.New(SelectedDestination?.Id); StatusMessage = "Schedule removed."; });
        }, () => SelectedSchedule is not null);
    }
}
