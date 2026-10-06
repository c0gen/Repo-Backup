using System.Windows;
using Microsoft.Win32;

namespace RepoBackup.Desktop.Infrastructure;

public sealed class DialogService
{
    public Window? Owner => Application.Current.MainWindow;
    public void Error(Exception error) => MessageBox.Show(Owner!, error.Message, "Repo Backup", MessageBoxButton.OK, MessageBoxImage.Warning);
    public void Information(string message) => MessageBox.Show(Owner!, message, "Repo Backup", MessageBoxButton.OK, MessageBoxImage.Information);
    public string? Folder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title }; return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }
    public string? Save(string title, string filename, string filter)
    {
        var dialog = new SaveFileDialog { Title = title, FileName = filename, Filter = filter }; return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }
    public string? Open(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter }; return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }
}
