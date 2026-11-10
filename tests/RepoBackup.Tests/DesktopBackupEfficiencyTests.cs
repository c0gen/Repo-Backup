using System.Windows.Controls;
using System.Windows.Threading;
using RepoBackup.Core.Backup;
using RepoBackup.Core.Models;
using static RepoBackup.Tests.DesktopTestSupport;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class DesktopBackupEfficiencyTests
{
    public static async Task RunAsync(TestRunner tests, string root)
    {
        await tests.Run("Desktop backup defaults persist, one-run rereads reset, and settings render", async () =>
        {
            var model = Model(root, out var dialogs);
            var project = Project(model, root, "Read-mode fixture", approved: true);
            await File.WriteAllTextAsync(Path.Combine(project.Roots[0].Path, "file.txt"), "Desktop incremental fixture");
            var destination = await model.Services.Backups.AddDestinationAsync("Backup", Path.Combine(root, "repository"), protection: DestinationProtection.PasswordFree);
            await model.InitializeAsync(discover: false, showSetup: false);
            Assert(model.IncrementalBackups && !model.RereadAllSourceFiles, "Fresh default is not incremental.");
            model.IncrementalBackups = false;
            Assert(model.Services.Catalog.GetSetting<BackupReadMode>(BackupReadPolicy.Setting) == BackupReadMode.Full, "Default was not saved.");
            var reloaded = new Desktop.ViewModels.MainViewModel(model.Services, dialogs);
            await reloaded.InitializeAsync(discover: false, showSetup: false);
            Assert(!reloaded.IncrementalBackups, "Saved full-read default was not loaded.");
            model.IncrementalBackups = true; model.RereadAllSourceFiles = true;
            await ExecuteAsync(model.BackupAllCommand);
            await model.Queue.WaitForStopAsync();
            Assert(!model.RereadAllSourceFiles && model.Services.Catalog.History().First().ReadMode == BackupReadMode.Full, "One-run override not applied/reset.");
            Assert(model.Services.Catalog.GetSetting<BackupReadMode>(BackupReadPolicy.Setting) == BackupReadMode.Incremental, "One-run override changed the default.");
            await ExecuteAsync(model.BackupAllCommand);
            await model.Queue.WaitForStopAsync();
            Assert(model.Services.Catalog.History().First().ReadMode == BackupReadMode.Incremental && dialogs.Errors.Count == 0, "Next backup did not use incremental reads.");
            model.Section = "Settings";
            await WithWindowAsync(model, async window =>
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var controls = Descendants<CheckBox>(window).Where(c => c.Content is string text && (text.StartsWith("Use incremental") || text.StartsWith("Reread all"))).ToList();
                Assert(controls.Count == 2 && controls.All(c => c.IsEnabled), "Read mode controls are missing or disabled.");
                controls.Last().BringIntoView();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Render(window, Path.Combine(root, "backup-settings.png"));
                Render(window, Path.Combine(root, "backup-settings-150.png"), 1.5);
            });
        });
    }
}
