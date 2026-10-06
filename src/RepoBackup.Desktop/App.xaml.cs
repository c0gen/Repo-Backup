using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RepoBackup.Core.Application;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Desktop.Infrastructure;
using RepoBackup.Desktop.ViewModels;

namespace RepoBackup.Desktop;

public partial class App : System.Windows.Application
{
    private readonly InstallationLifetime installationLifetime = new();

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        installationLifetime.Dispose();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, failure) =>
        {
            var renderIndex = Array.IndexOf(e.Args, "--render-preview");
            if (renderIndex >= 0 && renderIndex + 1 < e.Args.Length) File.WriteAllText(e.Args[renderIndex + 1] + ".error.txt", failure.Exception.ToString());
            else MessageBox.Show(failure.Exception.Message, "Repo Backup", MessageBoxButton.OK, MessageBoxImage.Error);
            failure.Handled = true; Shutdown(1);
        };
        try
        {
            string? Argument(string name) { var index = Array.IndexOf(e.Args, name); return index >= 0 && index + 1 < e.Args.Length ? e.Args[index + 1] : null; }
            System.Diagnostics.TextWriterTraceListener? bindingTrace = null;
            if (Argument("--render-preview") is { } traceOutput)
            {
                bindingTrace = new(traceOutput + ".bindings.txt");
                System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(bindingTrace);
                System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = System.Diagnostics.SourceLevels.Error;
            }
            Func<string, long?>? previewSpace = Argument("--render-preview") is not null && e.Args.Contains("--synthetic-preview")
                ? _ => 128L * 1024 * 1024 * 1024 : null;
            var services = new ApplicationServices(Argument("--data-dir")); var model = new MainViewModel(services, new DialogService(), previewSpace);
            var window = new MainWindow(model); MainWindow = window;
            if (Argument("--render-preview") is { } output)
            {
                // Render the real WPF visual tree for layout QA without displaying a desktop window.
                window.ShowInTaskbar = false; window.Width = 1500; window.Height = 960;
                if (Argument("--preview-project") is { } name) model.SelectedProject = model.Projects.FirstOrDefault(p => p.Name == name);
                foreach (var item in model.Projects.Where(p => p.Enabled && p.Available).Take(2)) item.IsSelected = true;
                if (model.SelectedProject is not null) model.SelectedProject.IsSelected = true;
                if (Argument("--render-section") is { } section) model.Section = section;
                await model.RefreshPreviewAsync();
                if (model.IsSnapshots)
                {
                    var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
                    while (model.IsBusy && DateTimeOffset.UtcNow < deadline) await Task.Delay(25);
                    if (model.IsBusy) throw new TimeoutException("Snapshot preview did not finish.");
                    await model.RefreshSnapshotFilesAsync();
                }
                var surface = (System.Windows.Controls.Panel)window.Content;
                surface.Background = window.Background;
                surface.Measure(new Size(1500, 960)); surface.Arrange(new Rect(0, 0, 1500, 960)); surface.UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var bitmap = new RenderTargetBitmap(1500, 960, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(output)) encoder.Save(file);
                bindingTrace?.Flush(); bindingTrace?.Close();
                Shutdown(); return;
            }
            window.Show(); await model.InitializeAsync();
        }
        catch (Exception error)
        {
            var renderIndex = Array.IndexOf(e.Args, "--render-preview");
            if (renderIndex >= 0 && renderIndex + 1 < e.Args.Length) File.WriteAllText(e.Args[renderIndex + 1] + ".error.txt", error.ToString());
            else MessageBox.Show(error.Message, "Repo Backup startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
