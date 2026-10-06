using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using RepoBackup.Core.Discovery;
using RepoBackup.Core.Models;
using RepoBackup.Desktop.Infrastructure;
using RepoBackup.Desktop.ViewModels;
using RepoBackup.Desktop.Views;
using static RepoBackup.Tests.TestRunner;
using static RepoBackup.Tests.DesktopTestSupport;

namespace RepoBackup.Tests;

public static class DesktopSetupTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        await tests.Run("Setup opens before discovery, persists dismissal, and resumes without another popup", async () =>
        {
            var model = Model(Path.Combine(root, "first"), out var dialogs); var discoveries = 0;
            model.DiscoveryLoader = _ => { discoveries++; return Task.FromResult(new DiscoveryResult(0, 0, 0, [])); };
            Assert(model.Projects.Count == 0 && !model.CatalogLoad.HasLoaded, "Construction performed a catalog load.");
            await model.InitializeAsync();
            Assert(model.Setup.IsOpen && model.Setup.IsDestination && discoveries == 0 && !model.BackupAllCommand.CanExecute(null), "Fresh startup skipped destination setup or ran discovery too soon.");
            await WithWindowAsync(model, async window =>
            {
                foreach (var scale in new[] { 1d, 1.5d, 2d }) Render(window, Path.Combine(root, $"setup-destination-{scale}.png"), scale);
                var wizard = Descendants<SetupWizardView>(window).Single();
                Assert(KeyboardNavigation.GetTabNavigation(wizard) == KeyboardNavigationMode.Cycle, "Wizard does not keep keyboard navigation inside setup.");
                var fields = Descendants<TextBox>(wizard).Where(c => c.IsVisible && c.IsEnabled).ToList();
                Assert(fields.Count >= 2 && fields.All(c => Inside(c, window)), "Destination inputs are clipped.");
                Assert(fields[0].Focus(), "Destination input cannot take keyboard focus.");
                fields[0].MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                Assert(Keyboard.FocusedElement is DependencyObject focused && wizard.IsAncestorOf(focused), "Focus escaped the wizard.");
                await ExecuteAsync(model.Setup.LaterCommand);
                Assert(model.Setup.ShowResume && !model.Setup.IsOpen, "Dismissal did not expose a resume hint.");
                Render(window, Path.Combine(root, "setup-resume.png"));
            });
            var resumed = Model(Path.Combine(root, "first"), out _); await resumed.InitializeAsync();
            Assert(!resumed.Setup.IsOpen && resumed.Setup.ShowResume, "Dismissed setup popped up on relaunch.");
            await ExecuteAsync(resumed.Setup.OpenCommand);
            Assert(resumed.Setup.IsDestination, "Resume did not choose the missing prerequisite.");
            Assert(dialogs.Errors.Count == 0 && model.Services.Catalog.History().Count == 0 && model.Services.Catalog.Schedules().Count == 0, "Setup started work without a backup action.");
        });
        await tests.Run("Setup destination failures and cancellation stay retryable without advancing", async () =>
        {
            var fixture = Path.Combine(root, "destination-errors"); var model = Model(fixture, out _); await model.InitializeAsync();
            var input = new DestinationInput("Fixture", Path.Combine(fixture, "backup"), null, false, DestinationProtection.PasswordFree);
            model.DestinationCreator = (_, _) => throw new IOException("Fixture drive unavailable");
            await model.Setup.SaveDestinationAsync(input);
            Assert(model.Setup.IsDestination && model.Setup.DestinationLoad.HasError && !model.IsBusy && model.Destinations.Count == 0, "Failed destination advanced setup or lost its error.");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            model.DestinationCreator = async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); };
            var save = model.Setup.SaveDestinationAsync(input); await entered.Task;
            Assert(model.IsBusy && model.Setup.DestinationLoad.IsLoading && !model.Setup.LaterCommand.CanExecute(null), "Destination write could be abandoned while running.");
            model.Cancel(); await save;
            Assert(model.Setup.IsDestination && !model.Setup.DestinationLoad.IsLoading && model.Destinations.Count == 0, "Cancellation advanced setup or left loading stuck.");
        });
        await tests.Run("The wizard primary action reads the shared form and preserves a repository after failed key export", async () =>
        {
            var fixture = Path.Combine(root, "primary-action"); var model = Model(fixture, out var dialogs); await model.InitializeAsync();
            var reachedDiscovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            model.DiscoveryLoader = _ => { reachedDiscovery.TrySetResult(); return Task.FromResult(new DiscoveryResult(0, 0, 0, [])); };
            await WithWindowAsync(model, async window =>
            {
                var wizard = Descendants<SetupWizardView>(window).Single();
                var form = (DestinationForm)wizard.FindName("DestinationForm");
                ((TextBox)form.FindName("PathInput")).Text = Path.Combine(fixture, "backup");
                var next = Descendants<Button>(wizard).Single(b => b.Content is string text && text == "Save and continue");
                Assert(next.IsEnabled, "The first-run primary action cannot submit the destination form.");
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await reachedDiscovery.Task.WaitAsync(TimeSpan.FromSeconds(30));
                while (model.DiscoveryLoad.IsLoading) await Task.Delay(10);
                Assert(model.Setup.IsDiscovery && model.SelectedDestination?.Protection == DestinationProtection.PasswordFree && model.HasConfiguredDestination, "The primary action did not create the configured destination.");
            });
            var protectedModel = Model(Path.Combine(root, "export-failure"), out var protectedDialogs); await protectedModel.InitializeAsync();
            var repositoryPath = Path.Combine(root, "export-failure", "backup");
            protectedDialogs.SavePath = Path.Combine(repositoryPath, "unsafe.key");
            await protectedModel.Setup.SaveDestinationAsync(new("Protected", repositoryPath, null, false, DestinationProtection.RecoveryKey));
            Assert(protectedModel.HasConfiguredDestination && protectedModel.Setup.DestinationLoad.HasError && !protectedModel.Setup.IsCreatingDestination && protectedModel.Setup.CanContinue, "A failed key export trapped the user in duplicate repository creation.");
            Assert(protectedModel.Services.Credentials.Exists(protectedModel.SelectedDestination!.Id), "Failed export lost the saved recovery key.");
            await ExecuteAsync(protectedModel.Setup.NextCommand);
            Assert(protectedModel.Setup.IsDiscovery && dialogs.Errors.Count == 0, "A saved destination could not continue after export failed.");
        });
        await tests.Run("Wizard reuses protected creation and export, reviews discovery, and hands off without backup", async () =>
        {
            var fixture = Path.Combine(root, "journey"); var model = Model(fixture, out var dialogs); await model.InitializeAsync();
            var alpha = Project(model, fixture, "Alpha"); var beta = Project(model, fixture, "Beta");
            Project(model, fixture, "Missing", available: false);
            var discovered = 0;
            model.DiscoveryLoader = _ => { discovered++; return Task.FromResult(new DiscoveryResult(0, 3, 3, ["Fixture provider warning: another provider is unavailable."])); };
            dialogs.SavePath = Path.Combine(fixture, "recovery.key");
            await model.Setup.SaveDestinationAsync(new("Backup drive", Path.Combine(fixture, "backup"), null, false, DestinationProtection.RecoveryKey));
            Assert(model.Setup.IsDiscovery && discovered == 1 && dialogs.SaveCalls == 1 && File.Exists(dialogs.SavePath) && model.HasDiscoveryWarnings, "Wizard skipped discovery, provider warnings, or key export.");
            Assert(!model.Setup.CanContinue, "Wizard advanced without an included project.");
            await ExecuteAsync(model.ApproveCommand, model.Discoveries.Single(p => p.Id == alpha.Id));
            await ExecuteAsync(model.DismissCommand, model.Discoveries.Single(p => p.Id == beta.Id));
            await ExecuteAsync(model.ApproveCommand, model.Discoveries.Single(p => p.Name == "Missing"));
            Assert(model.Setup.CanContinue && model.DismissedProjects.Count == 1, "Review actions did not update setup prerequisites.");
            await WithWindowAsync(model, async window =>
            {
                Render(window, Path.Combine(root, "setup-discovery.png"));
                await ExecuteAsync(model.Setup.NextCommand);
                Assert(model.Setup.IsReview && model.Setup.ReviewDestination.Contains("Backup drive"), "Review summary lost the destination.");
                Render(window, Path.Combine(root, "setup-review.png"));
                await ExecuteAsync(model.Setup.BackCommand); Assert(model.Setup.IsDiscovery, "Back did not return to project review.");
                await ExecuteAsync(model.Setup.NextCommand); await ExecuteAsync(model.Setup.NextCommand);
                Assert(!model.Setup.IsOpen && !model.Setup.ShowResume && model.IsProjects && model.SelectedCount == 1 && model.SelectedProject?.Id == alpha.Id, "Final handoff failed to select the available included projects.");
            });
            Assert(model.Services.Catalog.History().Count == 0 && model.Services.Catalog.Schedules().Count == 0 && dialogs.Errors.Count == 0, "Finishing setup started a backup or schedule.");
            var relaunch = Model(fixture, out _); await relaunch.InitializeAsync(discover: false);
            Assert(!relaunch.Setup.IsOpen && !relaunch.Setup.ShowResume, "Completed setup reappeared.");
            var recovery = Model(Path.Combine(root, "open-existing"), out var recoveryDialogs); await recovery.InitializeAsync();
            await recovery.Setup.SaveDestinationAsync(new("Existing backups", model.SelectedDestination!.Path, await File.ReadAllTextAsync(dialogs.SavePath), true, DestinationProtection.RecoveryKey));
            Assert(recovery.Setup.IsDiscovery && recovery.SelectedDestination?.RepositoryId == model.SelectedDestination.RepositoryId && recoveryDialogs.SaveCalls == 0 && recovery.Services.Catalog.Projects().Count == 0, "Opening existing backups changed identity or auto-enabled recovered projects.");
        });
        await tests.Run("Empty discovery, unavailable sources, manual folders, and configured upgrades have usable paths", async () =>
        {
            var fixture = Path.Combine(root, "empty"); var model = Model(fixture, out var dialogs); await model.InitializeAsync();
            Destination(model, fixture); await model.ReloadAsync(); await ExecuteAsync(model.Setup.NextCommand);
            Assert(model.Setup.IsDiscovery && model.ShowDiscoveriesEmpty && !model.Setup.CanContinue, "Empty discovery did not explain how to proceed.");
            var missing = Project(model, fixture, "Missing", available: false); await model.ReloadAsync();
            await ExecuteAsync(model.ApproveCommand, model.Discoveries.Single(p => p.Id == missing.Id));
            Assert(!model.Setup.CanContinue, "An unavailable source alone was treated as ready to back up.");
            dialogs.FolderPath = Path.Combine(fixture, "Manual"); Directory.CreateDirectory(dialogs.FolderPath);
            await ExecuteAsync(model.AddFolderCommand);
            Assert(model.Setup.CanContinue && model.Projects.Single(p => p.Name == "Manual").Enabled, "Manual folder could not complete project selection.");
            var configured = Model(Path.Combine(root, "configured"), out _); Destination(configured, Path.Combine(root, "configured"));
            await configured.InitializeAsync(discover: false);
            Assert(!configured.Setup.IsOpen && !configured.Setup.ShowResume && configured.IsProjects, "Existing configured installation was forced through onboarding.");
        });
    }
}
