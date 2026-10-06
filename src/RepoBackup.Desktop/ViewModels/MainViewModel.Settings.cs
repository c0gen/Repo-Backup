using RepoBackup.Core.Models;
using RepoBackup.Core.Windows;
using RepoBackup.Core.Discovery;

namespace RepoBackup.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private bool useVss;
    public bool UseVss { get => useVss; set { if (Set(ref useVss, value)) Services.Catalog.SaveSetting("useVss", value); } }
    private bool enableSchedule;
    public bool EnableSchedule { get => enableSchedule; set => Set(ref enableSchedule, value); }
    private ScheduleKind scheduleKind;
    public ScheduleKind ScheduleKind { get => scheduleKind; set => Set(ref scheduleKind, value); }
    private string scheduleTime = "18:00";
    public string ScheduleTime { get => scheduleTime; set => Set(ref scheduleTime, value); }
    private DayOfWeek scheduleDay = DayOfWeek.Friday;
    public DayOfWeek ScheduleDay { get => scheduleDay; set => Set(ref scheduleDay, value); }
    private string scheduleHours = "24";
    public string ScheduleHours { get => scheduleHours; set => Set(ref scheduleHours, value); }
    private SelectionOption? scheduleScope;
    public SelectionOption? ScheduleScope { get => scheduleScope; set => Set(ref scheduleScope, value); }
    private ScheduleDefinition? selectedSchedule;
    public ScheduleDefinition? SelectedSchedule
    {
        get => selectedSchedule;
        set
        {
            if (!Set(ref selectedSchedule, value) || value is null) return;
            EnableSchedule = value.Enabled; ScheduleKind = value.Kind; ScheduleTime = value.Time.ToString("HH:mm"); ScheduleDay = value.Day; ScheduleHours = value.IntervalHours.ToString();
            ScheduleScope = ScheduleScopes.FirstOrDefault(s => s.Selection?.Id == value.SelectionId);
            SelectedDestination = Destinations.FirstOrDefault(d => d.Id == value.DestinationId);
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
        SaveScheduleCommand = Command(async () =>
        {
            if (!TimeOnly.TryParse(ScheduleTime, out var time)) throw new ArgumentException("Enter a valid schedule time, such as 18:00.");
            if (!int.TryParse(ScheduleHours, out var hours)) throw new ArgumentException("Enter an interval in hours.");
            var schedule = new ScheduleDefinition { Id = SelectedSchedule?.Id ?? Guid.NewGuid().ToString("N"), DestinationId = SelectedDestination!.Id, SelectionId = ScheduleScope?.Selection?.Id, Enabled = EnableSchedule, Kind = ScheduleKind, Time = time, Day = ScheduleDay, IntervalHours = hours };
            await Services.Scheduler.SaveAsync(schedule); Reload(); StatusMessage = schedule.Enabled ? "Schedule saved. Backups can run while the GUI is closed." : "Schedule saved disabled.";
        }, () => SelectedDestination is not null);
        DeleteScheduleCommand = Command(async () => { await Services.Scheduler.DeleteAsync(SelectedSchedule!.Id); SelectedSchedule = null; Reload(); StatusMessage = "Schedule removed."; }, () => SelectedSchedule is not null);
    }
}
