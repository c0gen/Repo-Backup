using System.Windows.Input;

namespace RepoBackup.Desktop.Infrastructure;

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
}
public sealed class AsyncCommand(Func<Task> execute, Action<Exception> error, Func<bool>? canExecute = null) : ICommand
{
    private bool running;
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? parameter) => !running && (canExecute?.Invoke() ?? true);
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        running = true; CommandManager.InvalidateRequerySuggested();
        try { await execute(); } catch (OperationCanceledException) { } catch (Exception e) { error(e); }
        finally { running = false; CommandManager.InvalidateRequerySuggested(); }
    }
}
