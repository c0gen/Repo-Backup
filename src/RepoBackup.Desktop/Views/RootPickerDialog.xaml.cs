using System.Windows;
using RepoBackup.Core.Models;

namespace RepoBackup.Desktop.Views;

public partial class RootPickerDialog : Window
{
    public SourceRoot? SelectedRoot => RootsList.SelectedItem as SourceRoot;
    public RootPickerDialog(IEnumerable<SourceRoot> roots) { InitializeComponent(); RootsList.ItemsSource = roots; RootsList.SelectedIndex = 0; }
    private void LocateClick(object sender, RoutedEventArgs e) { if (SelectedRoot is not null) DialogResult = true; }
}
