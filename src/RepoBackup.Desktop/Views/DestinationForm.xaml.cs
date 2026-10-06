using System.Windows;
using System.Windows.Controls;
using RepoBackup.Desktop.Infrastructure;
using Microsoft.Win32;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Backup;
using RepoBackup.Core.Models;

namespace RepoBackup.Desktop.Views;

public partial class DestinationForm : UserControl
{
    public Func<bool>? ConfirmRecoveryKey { get; set; }
    public DestinationInput Input => new(DestinationName, DestinationPath, RecoveryKey, OpenExisting, Protection);
    public string DestinationName => NameInput.Text;
    public string DestinationPath => PathInput.Text;
    public bool OpenExisting => ExistingInput.IsChecked == true;
    public DestinationProtection Protection => ProtectionInput.SelectedItem is DestinationProtection mode ? mode : DestinationProtection.PasswordFree;
    public string? RecoveryKey => Protection == DestinationProtection.PasswordFree || string.IsNullOrEmpty(KeyInput.Password) ? null : KeyInput.Password;
    public DestinationForm()
    {
        InitializeComponent();
        ProtectionInput.ItemsSource = new[] { DestinationProtection.PasswordFree, DestinationProtection.RecoveryKey };
        ProtectionInput.SelectedItem = DestinationProtection.PasswordFree;
    }
    private void ProtectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (KeySection is null || PasswordFreeHelp is null) return;
        var protectedMode = Protection == DestinationProtection.RecoveryKey;
        KeySection.Visibility = protectedMode ? Visibility.Visible : Visibility.Collapsed;
        PasswordFreeHelp.Visibility = protectedMode ? Visibility.Collapsed : Visibility.Visible;
        if (!protectedMode) KeyInput.Clear();
    }
    public void Reset()
    {
        NameInput.Text = "Backup Drive"; PathInput.Clear(); KeyInput.Clear();
        ExistingInput.IsChecked = false; ProtectionInput.SelectedItem = DestinationProtection.PasswordFree;
    }
    private void BrowseClick(object sender, RoutedEventArgs e) { var dialog = new OpenFolderDialog(); if (dialog.ShowDialog(Window.GetWindow(this)) == true) PathInput.Text = dialog.FolderName; }
    private void KeyFileClick(object sender, RoutedEventArgs e) { try { var dialog = new OpenFileDialog { Filter = "Recovery key|*.key|All files|*.*" }; if (dialog.ShowDialog(Window.GetWindow(this)) == true) KeyInput.Password = File.ReadAllText(dialog.FileName).Trim(); } catch (Exception error) { MessageBox.Show(Window.GetWindow(this), error.Message, "Recovery key", MessageBoxButton.OK, MessageBoxImage.Warning); } }
    private bool ConfirmProtection() => ConfirmRecoveryKey?.Invoke() ?? MessageBox.Show(Window.GetWindow(this), BackupService.RecoveryKeyWarning,
        "Keep your recovery key safe", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
    public bool TryAccept()
    {
        PathSafety.Normalize(DestinationPath);
        if (string.IsNullOrWhiteSpace(DestinationName)) throw new ArgumentException("Enter a destination name.");
        if (Protection == DestinationProtection.RecoveryKey)
        {
            if (OpenExisting && RecoveryKey is null) throw new ArgumentException("Load the existing repository's recovery key.");
            if (RecoveryKey is { Length: < 16 }) throw new ArgumentException("Recovery key must contain at least 16 characters.");
            if (!OpenExisting && !ConfirmProtection()) return false;
        }
        return true;
    }
}
