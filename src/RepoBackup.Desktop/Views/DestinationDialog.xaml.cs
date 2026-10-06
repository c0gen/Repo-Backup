using System.Windows;
using Microsoft.Win32;
using RepoBackup.Core.Infrastructure;

namespace RepoBackup.Desktop.Views;

public partial class DestinationDialog : Window
{
    public string DestinationName => NameInput.Text;
    public string DestinationPath => PathInput.Text;
    public bool OpenExisting => ExistingInput.IsChecked == true;
    public string? RecoveryKey => string.IsNullOrEmpty(KeyInput.Password) ? null : KeyInput.Password;
    public DestinationDialog() => InitializeComponent();
    private void BrowseClick(object sender, RoutedEventArgs e) { var dialog = new OpenFolderDialog(); if (dialog.ShowDialog(this) == true) PathInput.Text = dialog.FolderName; }
    private void KeyFileClick(object sender, RoutedEventArgs e) { var dialog = new OpenFileDialog { Filter = "Recovery key|*.key|All files|*.*" }; if (dialog.ShowDialog(this) == true) KeyInput.Password = File.ReadAllText(dialog.FileName).Trim(); }
    private void SaveClick(object sender, RoutedEventArgs e)
    {
        try { PathSafety.Normalize(DestinationPath); if (string.IsNullOrWhiteSpace(DestinationName)) throw new ArgumentException("Enter a destination name."); if (OpenExisting && RecoveryKey is null) throw new ArgumentException("Load the existing repository's recovery key."); DialogResult = true; }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Backup destination", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
