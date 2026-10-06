using System.Windows;
using RepoBackup.Core.Models;
using RepoBackup.Desktop.Infrastructure;

namespace RepoBackup.Desktop.Views;

public partial class DestinationDialog : Window
{
    public DestinationForm Editor => Form;
    public DestinationInput Input => Form.Input;
    public string DestinationName => Form.DestinationName;
    public string DestinationPath => Form.DestinationPath;
    public bool OpenExisting => Form.OpenExisting;
    public DestinationProtection Protection => Form.Protection;
    public string? RecoveryKey => Form.RecoveryKey;
    public DestinationDialog() : this(null) { }
    public DestinationDialog(Func<bool>? confirmRecoveryKey)
    {
        InitializeComponent(); Form.ConfirmRecoveryKey = confirmRecoveryKey;
    }
    public bool TryAccept() => Form.TryAccept();
    private void SaveClick(object sender, RoutedEventArgs e)
    {
        try { if (TryAccept()) DialogResult = true; }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Backup destination", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
