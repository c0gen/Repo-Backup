using System.ComponentModel;
using System.Windows;
using RepoBackup.Desktop.ViewModels;

namespace RepoBackup.Desktop;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    public MainWindow(MainViewModel viewModel) { InitializeComponent(); ViewModel = viewModel; DataContext = viewModel; Closing += WindowClosing; }
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (!ViewModel.IsBusy) return;
        e.Cancel = true;
        if (MessageBox.Show(this, "An operation is running. Cancel it before closing?", "Repo Backup", MessageBoxButton.YesNo) == MessageBoxResult.Yes) ViewModel.Cancel();
    }
}
