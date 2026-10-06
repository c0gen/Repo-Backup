using RepoBackup.Core.Models;
using RepoBackup.Core.Windows;
using RepoBackup.Core.Discovery;

namespace RepoBackup.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private bool useVss;
    public bool UseVss { get => useVss; set { if (Set(ref useVss, value)) Services.Catalog.SaveSetting("useVss", value); } }
    public ScheduleEditorState ScheduleEditor { get; } = new();
    public Destination? SelectedScheduleDestination
    {
        get => ScheduleDestinations.FirstOrDefault(d => d.Id == ScheduleEditor.DestinationId);
        set => ScheduleEditor.DestinationId = value?.Id;
    }
    public SelectionOption? SelectedScheduleScope
    {
        get => ScheduleScopes.FirstOrDefault(s => s.Selection?.Id == ScheduleEditor.SelectionId);
        set => ScheduleEditor.SelectionId = value?.Selection?.Id;
    }
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
    private string codexHookStatus = "Loading…", claudeHookStatus = "Loading…", antigravityHookStatus = "Loading…";
    public string CodexHookStatus => codexHookStatus;
    public string ClaudeHookStatus => claudeHookStatus;
    public string AntigravityHookStatus => antigravityHookStatus;

    private void RaiseHookStatus()
    {
        Raise(nameof(CodexHookStatus)); Raise(nameof(ClaudeHookStatus)); Raise(nameof(AntigravityHookStatus));
    }

    private async Task InstallDiscoveryHookAsync(string provider)
    {
        await Task.Run(() => DiscoveryHookInstaller.Install(provider, Services.Catalog, Services.Paths.DataDirectory));
        await ReloadAsync();
        RaiseHookStatus(); StatusMessage = DiscoveryProviders.DisplayName(provider) + " hook configured; awaiting verification.";
        dialogs.Information("The hook records review candidates. Existing configuration was preserved. " + DiscoveryHookInstaller.Instructions(provider));
    }
    private void InitializeSettingsCommands()
    {
        ExportCatalogCommand = Command(async () => { var path = dialogs.Save("Export catalog — credentials are excluded", "RepoBackup-catalog.json", "JSON|*.json"); if (path is not null) { await File.WriteAllTextAsync(path, await Task.Run(Services.Catalog.Export)); StatusMessage = "Catalog exported without credentials. Schedules import disabled."; } });
        ImportCatalogCommand = Command(async () => { var path = dialogs.Open("Import a Repo Backup catalog", "JSON|*.json"); if (path is not null) { var json = await File.ReadAllTextAsync(path); await Task.Run(() => Services.Catalog.Import(json)); await ReloadAsync(); StatusMessage = "Catalog imported. Recovery keys must be supplied separately."; } });
        InstallHookCommand = Command(() => InstallDiscoveryHookAsync(DiscoveryProviders.Codex));
        InstallClaudeHookCommand = Command(() => InstallDiscoveryHookAsync(DiscoveryProviders.ClaudeCode));
        InstallAntigravityHookCommand = Command(() => InstallDiscoveryHookAsync(DiscoveryProviders.Antigravity));
        NewScheduleCommand = Command(() => { SelectedSchedule = null; ScheduleEditor.New(SelectedDestination?.Id); return Task.CompletedTask; }, () => CanEditSchedule);
        SaveScheduleCommand = Command(async () =>
        {
            var schedule = ScheduleEditor.Build();
            await OperateAsync(async token =>
            {
                ProgressStage = "Saving schedule…";
                await Task.Run(() => Services.Scheduler.SaveAsync(schedule, token), token); ScheduleEditor.Saved(schedule);
                StatusMessage = schedule.Enabled ? "Schedule saved. Backups can run while the GUI is closed." : "Schedule saved disabled.";
            });
        }, () => CanEditSchedule && ScheduleEditor.DestinationId is not null);
        DeleteScheduleCommand = Command(async () =>
        {
            var id = SelectedSchedule!.Id;
            await OperateAsync(async token => { ProgressStage = "Removing schedule…"; await Task.Run(() => Services.Scheduler.DeleteAsync(id, token), token); SelectedSchedule = null; ScheduleEditor.New(SelectedDestination?.Id); StatusMessage = "Schedule removed."; });
        }, () => CanEditSchedule && SelectedSchedule is not null);
    }
}
