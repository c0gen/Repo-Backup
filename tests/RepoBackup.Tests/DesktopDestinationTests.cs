using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RepoBackup.Core.Application;
using RepoBackup.Core.Models;
using RepoBackup.Desktop.Infrastructure;
using RepoBackup.Desktop.ViewModels;
using RepoBackup.Desktop.Views;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class DesktopDestinationTests
{
    public static Task RunAsync(TestRunner tests, string root) => OnStaAsync(async () =>
    {
        var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/RepoBackup;component/Themes/Dark.xaml", UriKind.Relative) });
        var app = new ApplicationServices(Path.Combine(root, "catalog"), discoveryProviders: []);
        var dialogs = new FixtureDialogs(); var model = new MainViewModel(app, dialogs); await model.ReloadAsync();
        using var bindingErrors = new StringWriter(); using var listener = new TextWriterTraceListener(bindingErrors);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            await tests.Run("Desktop protected warning can be cancelled without creating a repository", async () =>
            {
                var warned = 0; var path = Path.Combine(root, "cancelled");
                dialogs.Next = Form(path, () => { warned++; return false; });
                SelectProtection(dialogs.Next, DestinationProtection.RecoveryKey);
                model.AddDestinationCommand.Execute(null); await FinishAsync(model.AddDestinationCommand);
                Assert(warned == 1 && app.Catalog.Destinations().Count == 0 && !Directory.Exists(path) && dialogs.SaveCalls == 0, "Cancelled warning created a repository or offered export.");
            });
            await tests.Run("Desktop protected creation immediately offers a working key export", async () =>
            {
                var warned = 0;
                dialogs.Next = Form(Path.Combine(root, "protected"), () => { warned++; return true; });
                SelectProtection(dialogs.Next, DestinationProtection.RecoveryKey);
                Render((FrameworkElement)dialogs.Next.Content, 560, dialogs.Next.Background, Path.Combine(root, "protected-dialog.png"));
                dialogs.SavePath = Path.Combine(root, "immediate.key");
                model.AddDestinationCommand.Execute(null); await FinishAsync(model.AddDestinationCommand);
                var destination = model.SelectedDestination!;
                Assert(warned == 1 && dialogs.SaveCalls == 1 && destination.Protection == DestinationProtection.RecoveryKey, "Protected creation omitted warning or immediate export.");
                Assert(await File.ReadAllTextAsync(dialogs.SavePath) == app.Credentials.Get(destination.Id), "Immediate export used a different destination's key.");
            });
            await tests.Run("Desktop skipped key export leaves a usable destination and can be completed later", async () =>
            {
                dialogs.Next = Form(Path.Combine(root, "skipped-export"), () => true); SelectProtection(dialogs.Next, DestinationProtection.RecoveryKey);
                dialogs.SavePath = null;
                model.AddDestinationCommand.Execute(null); await FinishAsync(model.AddDestinationCommand);
                var destination = model.SelectedDestination!;
                Assert(model.StatusMessage.Contains("export skipped") && app.Credentials.Exists(destination.Id), "Skipped export lost its key or reminder.");
                Assert((await app.Backups.VerifyAsync(destination)).Outcome == Outcome.Successful, "Skipping export made the repository unusable.");
                dialogs.SavePath = Path.Combine(root, "later.key");
                model.ExportKeyCommand.Execute(null); await FinishAsync(model.ExportKeyCommand);
                Assert(await File.ReadAllTextAsync(dialogs.SavePath) == app.Credentials.Get(destination.Id), "Later export lost the generated key.");
            });
            await tests.Run("Desktop defaults to password-free protection without warning or export", async () =>
            {
                var warned = 0; var saves = dialogs.SaveCalls;
                dialogs.Next = Form(Path.Combine(root, "password-free"), () => { warned++; return true; });
                Assert(dialogs.Next.Protection == DestinationProtection.PasswordFree && ((StackPanel)dialogs.Next.Editor.FindName("KeySection")).Visibility == Visibility.Collapsed, "Desktop default or key visibility is incorrect.");
                Render((FrameworkElement)dialogs.Next.Content, 560, dialogs.Next.Background, Path.Combine(root, "password-free-dialog.png"));
                model.AddDestinationCommand.Execute(null); await FinishAsync(model.AddDestinationCommand);
                Assert(warned == 0 && dialogs.SaveCalls == saves && model.SelectedDestination?.Protection == DestinationProtection.PasswordFree && !model.ExportKeyCommand.CanExecute(null), "Password-free creation warned, offered export or enabled key export.");
                var page = new DestinationsPage { DataContext = model };
                Render(page, 1200, page.Background, Path.Combine(root, "destinations-page.png"), 960);
            });
            await tests.Run("Desktop opens password-free backups on a fresh catalog without a key", async () =>
            {
                var fresh = new ApplicationServices(Path.Combine(root, "fresh-catalog"), discoveryProviders: []);
                var freshDialogs = new FixtureDialogs { Next = Form(model.SelectedDestination!.Path, () => throw new InvalidOperationException("Opening should not warn.")) };
                ((CheckBox)freshDialogs.Next.Editor.FindName("ExistingInput")).IsChecked = true;
                var freshModel = new MainViewModel(fresh, freshDialogs); await freshModel.ReloadAsync();
                freshModel.AddDestinationCommand.Execute(null); await FinishAsync(freshModel.AddDestinationCommand);
                Assert(freshModel.SelectedDestination?.RepositoryId == model.SelectedDestination.RepositoryId && freshDialogs.SaveCalls == 0 && freshDialogs.Errors.Count == 0, "Fresh password-free opening required a key or export.");
            });
            await DesktopSetupTests.RunAsync(tests, Path.Combine(root, "setup"));
            await DesktopLoadingTests.RunAsync(tests, Path.Combine(root, "loading"));
            await DesktopWorkflowRegressionTests.RunAsync(tests, Path.Combine(root, "workflow"));
            await DesktopRestoreProgressTests.RunAsync(tests, Path.Combine(root, "restore-progress"));
            await tests.Run("Desktop protection switching clears unused key inputs and rendering has no binding errors", () =>
            {
                var form = Form(Path.Combine(root, "switch-mode"), () => true); SelectProtection(form, DestinationProtection.RecoveryKey);
                ((PasswordBox)form.Editor.FindName("KeyInput")).Password = "fixture-key-that-is-no-longer-needed";
                SelectProtection(form, DestinationProtection.PasswordFree);
                Assert(form.RecoveryKey is null && ((PasswordBox)form.Editor.FindName("KeyInput")).Password.Length == 0, "Mode switching retained an unused key.");
                listener.Flush(); Assert(bindingErrors.ToString().Length == 0 && dialogs.Errors.Count == 0, "Desktop binding or command errors: " + bindingErrors + string.Join("; ", dialogs.Errors.Select(e => e.Message)));
                return Task.CompletedTask;
            });
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); }
    });

    private static DestinationDialog Form(string path, Func<bool> confirm)
    {
        var form = new DestinationDialog(confirm);
        ((TextBox)form.Editor.FindName("PathInput")).Text = path;
        return form;
    }
    private static void SelectProtection(DestinationDialog form, DestinationProtection protection) => ((ComboBox)form.Editor.FindName("ProtectionInput")).SelectedItem = protection;
    private static async Task FinishAsync(ICommand command)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!command.CanExecute(null) && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);
        Assert(command.CanExecute(null), "Desktop command did not finish within 30 seconds.");
    }
    private static void Render(FrameworkElement surface, double width, Brush background, string file, double? viewportHeight = null)
    {
        if (surface is Control control) control.Background = background;
        surface.Measure(new Size(width, viewportHeight ?? double.PositiveInfinity));
        var height = viewportHeight ?? Math.Ceiling(surface.DesiredSize.Height);
        surface.Arrange(new Rect(0, 0, width, height)); surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(file); encoder.Save(output);
    }
    private static Task OnStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.InvokeShutdown(); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Isolated desktop fixture" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task;
    }
    private sealed class FixtureDialogs : DialogService
    {
        public DestinationDialog? Next { get; set; }
        public string? SavePath { get; set; }
        public int SaveCalls { get; private set; }
        public List<Exception> Errors { get; } = [];
        public override DestinationDialog? Destination() => Next?.TryAccept() == true ? Next : null;
        public override string? Save(string title, string filename, string filter) { SaveCalls++; return SavePath; }
        public override void Error(Exception error) => Errors.Add(error);
    }
}
