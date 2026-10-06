using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using RepoBackup.Desktop.ViewModels;

namespace RepoBackup.Desktop.Views;

public partial class SetupWizardView : UserControl
{
    public SetupWizardView() => InitializeComponent();
    private async void NextClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SetupWizardViewModel model || !model.CanChangeStep) return;
        try
        {
            if (model.IsDestination && model.IsCreatingDestination)
            {
                if (DestinationForm.TryAccept()) await model.SaveDestinationAsync(DestinationForm.Input);
                if (!model.IsCreatingDestination) DestinationForm.Reset();
            }
            else if (model.NextCommand.CanExecute(null)) model.NextCommand.Execute(null);
        }
        catch (Exception error) { model.DestinationLoad.SetError(error); }
    }
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) Dispatcher.BeginInvoke(() => MoveFocus(new TraversalRequest(FocusNavigationDirection.First)), DispatcherPriority.Input);
    }
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is SetupWizardViewModel model && model.LaterCommand.CanExecute(null))
        { model.LaterCommand.Execute(null); e.Handled = true; }
    }
}
