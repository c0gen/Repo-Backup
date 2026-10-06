using System.Windows;
using Microsoft.Win32;
using RepoBackup.Desktop.Views;

namespace RepoBackup.Desktop.Infrastructure;

public class DialogService
{
    public Window? Owner => Application.Current.MainWindow;
    public virtual void Error(Exception error) => MessageBox.Show(Owner!, error.Message, "Repo Backup", MessageBoxButton.OK, MessageBoxImage.Warning);
    public virtual void Information(string message) => MessageBox.Show(Owner!, message, "Repo Backup", MessageBoxButton.OK, MessageBoxImage.Information);
    public string? Folder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title }; return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }
    public virtual string? Save(string title, string filename, string filter)
    {
        var dialog = new SaveFileDialog { Title = title, FileName = filename, Filter = filter }; return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }
    public virtual DestinationDialog? Destination()
    {
        var dialog = new DestinationDialog { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog : null;
    }
    public string? Open(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter }; return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }
}
