using System.ComponentModel;
using System.Windows;
using RepoBackup.Desktop.ViewModels;

namespace RepoBackup.Desktop;

public partial class MainWindow : Window
{
    private bool closing, closeAllowed;
    public MainViewModel ViewModel { get; }
    public MainWindow(MainViewModel viewModel) { InitializeComponent(); ViewModel = viewModel; DataContext = viewModel; Closing += WindowClosing; }
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (closeAllowed || !ViewModel.IsBusy && !ViewModel.Queue.HasUnfinishedWork) { ViewModel.StopBackgroundReads(); return; }
        e.Cancel = true;
        if (closing) return;
        closing = true;
        try
        {
            if (await ViewModel.PrepareToCloseAsync())
            {
                closeAllowed = true;
                // The original Closing event must return before Close is called again.
                await Dispatcher.InvokeAsync(Close, System.Windows.Threading.DispatcherPriority.Background);
            }
        }
        finally { closing = false; }
    }
}
