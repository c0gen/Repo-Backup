using System.Windows;
using Microsoft.Win32;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Backup;
using RepoBackup.Core.Models;

namespace RepoBackup.Desktop.Views;

public partial class DestinationDialog : Window
{
    private readonly Func<bool> confirmRecoveryKey;
    public string DestinationName => NameInput.Text;
    public string DestinationPath => PathInput.Text;
    public bool OpenExisting => ExistingInput.IsChecked == true;
    public DestinationProtection Protection => ProtectionInput.SelectedItem is DestinationProtection mode ? mode : DestinationProtection.PasswordFree;
    public string? RecoveryKey => Protection == DestinationProtection.PasswordFree || string.IsNullOrEmpty(KeyInput.Password) ? null : KeyInput.Password;
    public DestinationDialog() : this(null) { }
    public DestinationDialog(Func<bool>? confirmRecoveryKey)
    {
        InitializeComponent();
        this.confirmRecoveryKey = confirmRecoveryKey ?? (() => MessageBox.Show(this, BackupService.RecoveryKeyWarning,
            "Keep your recovery key safe", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK);
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
    private void BrowseClick(object sender, RoutedEventArgs e) { var dialog = new OpenFolderDialog(); if (dialog.ShowDialog(this) == true) PathInput.Text = dialog.FolderName; }
    private void KeyFileClick(object sender, RoutedEventArgs e) { var dialog = new OpenFileDialog { Filter = "Recovery key|*.key|All files|*.*" }; if (dialog.ShowDialog(this) == true) KeyInput.Password = File.ReadAllText(dialog.FileName).Trim(); }
    private void SaveClick(object sender, RoutedEventArgs e)
    {
        try { if (TryAccept()) DialogResult = true; }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Backup destination", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    public bool TryAccept()
    {
        PathSafety.Normalize(DestinationPath);
        if (string.IsNullOrWhiteSpace(DestinationName)) throw new ArgumentException("Enter a destination name.");
        if (Protection == DestinationProtection.RecoveryKey)
        {
            if (OpenExisting && RecoveryKey is null) throw new ArgumentException("Load the existing repository's recovery key.");
            if (RecoveryKey is { Length: < 16 }) throw new ArgumentException("Recovery key must contain at least 16 characters.");
            if (!OpenExisting && !confirmRecoveryKey()) return false;
        }
        return true;
    }
}
