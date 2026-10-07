using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RepoBackup.Core.Application;
using RepoBackup.Core.Models;
using RepoBackup.Desktop;
using RepoBackup.Desktop.Infrastructure;
using RepoBackup.Desktop.ViewModels;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

internal static class DesktopTestSupport
{
    public static MainViewModel Model(string root, out TestDialogs dialogs)
    {
        dialogs = new TestDialogs();
        return new MainViewModel(new ApplicationServices(Path.Combine(root, "catalog"), discoveryProviders: []), dialogs, _ => 128L * 1024 * 1024 * 1024)
        {
            PreviewLoader = (_, _, _) => Task.FromResult(Preview()),
            SnapshotLoader = (_, _) => Task.FromResult(new List<SnapshotInfo>()),
            SnapshotFileLoader = (_, _, _) => Task.FromResult(new List<SnapshotFileItem>())
        };
    }
    public static Preview Preview(string warning = "") => new([], [], [], warning.Length == 0 ? [] : [warning], Coverage.FullProject);
    public static ProjectEntry Project(MainViewModel model, string root, string name, bool approved = false, bool available = true)
    {
        var path = Path.Combine(root, name);
        if (available) Directory.CreateDirectory(path);
        return model.Services.Catalog.RegisterCandidate(name, [path], "Fixture", approved: approved);
    }
    public static Destination Destination(MainViewModel model, string root, string name = "Backup drive")
    {
        var path = Path.Combine(root, name); Directory.CreateDirectory(path);
        var destination = new Destination { Name = name, Path = path, RepositoryId = new string('a', 64), Protection = DestinationProtection.PasswordFree };
        model.Services.Catalog.SaveDestination(destination); return destination;
    }
    public static async Task ExecuteAsync(ICommand command, object? parameter = null)
    {
        Assert(command.CanExecute(parameter), "Command was unexpectedly disabled.");
        command.Execute(parameter);
        if (command is AsyncCommand asyncCommand) await asyncCommand.Execution;
    }
    public static async Task WithWindowAsync(MainViewModel model, Func<MainWindow, Task> action, double width = 1140, double height = 730)
    {
        var window = new MainWindow(model) { Width = width, Height = height, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        try { window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await action(window); }
        finally { window.Close(); }
    }
    public static void Render(MainWindow window, string file, double scale = 1)
    {
        window.UpdateLayout(); var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)(window.ActualWidth * scale), (int)(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var output = File.Create(file); encoder.Save(output);
    }
    public static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index); if (child is T match) yield return match;
            foreach (var item in Descendants<T>(child)) yield return item;
        }
    }
    public static bool Inside(FrameworkElement element, FrameworkElement parent)
    {
        var bounds = element.TransformToAncestor(parent).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= parent.ActualWidth + 1 && bounds.Bottom <= parent.ActualHeight + 1;
    }
    internal sealed class TestDialogs : DialogService
    {
        public List<Exception> Errors { get; } = [];
        public List<string> InformationMessages { get; } = [];
        public Action<string>? OnInformation { get; set; }
        public string? FolderPath { get; set; }
        public string? SavePath { get; set; }
        public int SaveCalls { get; private set; }
        public override string? Folder(string title) => FolderPath;
        public override string? Save(string title, string filename, string filter) { SaveCalls++; return SavePath; }
        public override void Error(Exception error) => Errors.Add(error);
        public override void Information(string message) { InformationMessages.Add(message); OnInformation?.Invoke(message); }
    }
}
