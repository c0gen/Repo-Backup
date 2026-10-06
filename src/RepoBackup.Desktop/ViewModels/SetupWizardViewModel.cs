using System.Windows.Input;
using RepoBackup.Desktop.Infrastructure;

namespace RepoBackup.Desktop.ViewModels;

public sealed record SetupProgress(bool Presented = false, bool Completed = false);

public sealed class SetupWizardViewModel : ObservableObject
{
    private const string Setting = "desktopSetup";
    public MainViewModel Main { get; }
    private SetupProgress progress = new();
    private bool initialized, open;
    private bool creatingDestination;
    private int step;
    public LoadState DestinationLoad { get; } = new();
    public LoadState ProgressLoad { get; } = new();
    public bool IsOpen { get => open; private set { if (Set(ref open, value)) { Raise(nameof(IsClosed)); Raise(nameof(ShowResume)); } } }
    public bool IsClosed => !IsOpen;
    public bool ShowResume => initialized && !progress.Completed && !IsOpen;
    public int Step { get => step; private set { if (Set(ref step, value)) Refresh(); } }
    public bool IsDestination => Step == 0;
    public bool IsDiscovery => Step == 1;
    public bool IsReview => Step == 2;
    public string StepLabel => $"Step {Step + 1} of 3 · " + (Step == 0 ? "Choose a destination" : Step == 1 ? "Choose your projects" : "Ready to review");
    public bool IsCreatingDestination { get => creatingDestination; private set { if (Set(ref creatingDestination, value)) Refresh(); } }
    public bool ShowSavedDestination => !IsCreatingDestination && Main.HasDestinations;
    public string NextLabel => IsDestination && IsCreatingDestination ? "Save and continue" : IsReview ? "Open Projects" : "Continue";
    public string IncludedSummary { get { var count = Main.Projects.Count(p => p.Enabled && p.Available); return $"{count} available project{(count == 1 ? "" : "s")} included"; } }
    public string DiscoveryNextHint => Main.Projects.Any(p => p.Enabled && p.Available) ? "Continue to review your first backup." : "Include at least one available project to continue, or do this later.";
    public string ReviewDestination => Main.SelectedDestination is { } destination ? destination.Name + "\n" + destination.Path : "Choose a destination";
    public string ReviewProtection => Main.SelectedDestination?.Protection == Core.Models.DestinationProtection.RecoveryKey ? "Protected with a recovery key" : "No password required";
    public bool CanChangeStep => Main.IsIdle && !DestinationLoad.IsLoading && !ProgressLoad.IsLoading && !Main.CatalogLoad.IsLoading;
    public bool CanContinue => CanChangeStep && Main.CatalogLoad.IsReady && Main.HasConfiguredDestination &&
        (Step == 0 || (!Main.DiscoveryLoad.IsLoading && Main.Projects.Any(p => p.Enabled && p.Available)));
    public bool CanUseNextAction => IsDestination && IsCreatingDestination ? CanChangeStep && Main.CatalogLoad.IsReady : CanContinue;
    public ICommand OpenCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand LaterCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand NewDestinationCommand { get; }
    public ICommand UseSavedDestinationCommand { get; }

    public SetupWizardViewModel(MainViewModel main)
    {
        Main = main;
        OpenCommand = new AsyncCommand(OpenAsync, main.CatalogLoad.SetError, () => main.CanEditCatalog && main.IsIdle);
        BackCommand = new RelayCommand(_ => { Main.CancelDiscovery(); Step--; }, _ => CanChangeStep && Step > 0);
        LaterCommand = new RelayCommand(_ => Dismiss(), _ => CanChangeStep);
        NextCommand = new AsyncCommand(NextAsync, ProgressLoad.SetError, () => CanContinue);
        NewDestinationCommand = new RelayCommand(_ => IsCreatingDestination = true, _ => CanChangeStep);
        UseSavedDestinationCommand = new RelayCommand(_ => IsCreatingDestination = false, _ => CanChangeStep && Main.HasDestinations);
        DestinationLoad.PropertyChanged += (_, _) => Refresh();
        ProgressLoad.PropertyChanged += (_, _) => Refresh();
    }
    public async Task InitializeAsync(bool autoShow)
    {
        if (initialized) return;
        var saved = await Task.Run(() => Main.Services.Catalog.GetSetting<SetupProgress>(Setting));
        progress = saved ?? new SetupProgress(Completed: Main.Destinations.Any(d => d.RepositoryId is not null));
        initialized = true;
        if (autoShow && !progress.Presented && !progress.Completed) await OpenAsync();
        Refresh();
    }
    public async Task OpenAsync()
    {
        await PersistAsync(progress with { Presented = true });
        Main.CancelDiscovery();
        IsCreatingDestination = !Main.HasConfiguredDestination;
        Step = !Main.HasConfiguredDestination ? 0 : Main.Projects.Any(p => p.Enabled && p.Available) ? 2 : 1;
        IsOpen = true; Refresh();
        if (IsDiscovery) await Main.RefreshDiscoveryAsync();
    }
    public void Dismiss()
    {
        if (!CanChangeStep) return;
        Main.CancelDiscovery(); IsOpen = false;
    }
    public async Task SaveDestinationAsync(DestinationInput input)
    {
        using var request = DestinationLoad.Begin();
        try
        {
            await Main.AddDestinationAsync(input);
            DestinationLoad.Complete(request);
            if (!Main.CatalogLoad.IsReady) throw new InvalidOperationException("The repository was saved, but the catalog could not be refreshed. Retry loading before continuing.");
            IsCreatingDestination = false;
            Step = 1;
            await Main.RefreshDiscoveryAsync();
        }
        catch (OperationCanceledException) { DestinationLoad.Cancel(request); }
        catch (Exception error)
        {
            if (Main.SelectedDestination is { RepositoryId: not null } destination && destination.Path.Equals(Path.GetFullPath(input.Path), StringComparison.OrdinalIgnoreCase))
            {
                IsCreatingDestination = false;
                var nextStep = !Main.CatalogLoad.IsReady ? " Retry loading your configuration before continuing."
                    : destination.Protection == Core.Models.DestinationProtection.RecoveryKey ? " You can continue and export its recovery key from Destinations." : " You can continue with this saved destination.";
                error = new IOException("The repository is ready. " + error.Message + nextStep, error);
            }
            DestinationLoad.Fail(request, error);
        }
    }
    private async Task NextAsync()
    {
        if (!CanContinue) return;
        if (Step == 0) { DestinationLoad.Cancel(); Step = 1; await Main.RefreshDiscoveryAsync(); return; }
        if (Step == 1) { Step = 2; return; }
        await PersistAsync(progress with { Completed = true });
        Main.Search = ""; Main.ProjectFilter = "All projects"; Main.ActiveSelection = Main.Selections.FirstOrDefault();
        foreach (var project in Main.Projects) project.IsSelected = project.Enabled && project.Available;
        Main.SelectedProject = Main.Projects.FirstOrDefault(p => p.IsSelected);
        Main.Section = "Projects"; IsOpen = false; Refresh();
    }
    private async Task PersistAsync(SetupProgress next)
    {
        using var request = ProgressLoad.Begin();
        try
        {
            await Task.Run(() => Main.Services.Catalog.SaveSetting(Setting, next));
            progress = next; ProgressLoad.Complete(request);
        }
        catch (Exception error) { ProgressLoad.Fail(request, error); throw; }
    }
    public void Refresh()
    {
        foreach (var property in new[] { nameof(IsDestination), nameof(IsDiscovery), nameof(IsReview), nameof(StepLabel), nameof(NextLabel),
            nameof(IncludedSummary), nameof(ReviewDestination), nameof(ReviewProtection), nameof(CanChangeStep), nameof(CanContinue), nameof(ShowResume),
            nameof(CanUseNextAction), nameof(ShowSavedDestination), nameof(DiscoveryNextHint) }) Raise(property);
        CommandManager.InvalidateRequerySuggested();
    }
}
