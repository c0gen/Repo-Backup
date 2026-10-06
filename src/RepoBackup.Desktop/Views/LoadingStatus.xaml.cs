using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RepoBackup.Desktop.ViewModels;

namespace RepoBackup.Desktop.Views;

public partial class LoadingStatus : UserControl
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(nameof(State), typeof(LoadState), typeof(LoadingStatus));
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message), typeof(string), typeof(LoadingStatus), new PropertyMetadata("Loading…"));
    public static readonly DependencyProperty RetryCommandProperty = DependencyProperty.Register(nameof(RetryCommand), typeof(ICommand), typeof(LoadingStatus));
    public LoadState? State { get => (LoadState?)GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public string Message { get => (string)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public ICommand? RetryCommand { get => (ICommand?)GetValue(RetryCommandProperty); set => SetValue(RetryCommandProperty, value); }
    public LoadingStatus() => InitializeComponent();
}
