using System.Windows.Input;

namespace RepoBackup.Desktop.Infrastructure;

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
}
public sealed class AsyncCommand : ICommand
{
    private readonly Func<object?, Task> execute;
    private readonly Action<Exception> error;
    private readonly Func<bool>? canExecute;
    public Task Execution { get; private set; } = Task.CompletedTask;
    public AsyncCommand(Func<Task> execute, Action<Exception> error, Func<bool>? canExecute = null)
        : this(_ => execute(), error, canExecute) { }
    public AsyncCommand(Func<object?, Task> execute, Action<Exception> error, Func<bool>? canExecute = null)
    { this.execute = execute; this.error = error; this.canExecute = canExecute; }
    private bool running;
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? parameter) => !running && (canExecute?.Invoke() ?? true);
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        Execution = RunAsync(parameter);
        await Execution;
    }
    private async Task RunAsync(object? parameter)
    {
        running = true; CommandManager.InvalidateRequerySuggested();
        try { await execute(parameter); } catch (OperationCanceledException) { } catch (Exception e) { error(e); }
        finally { running = false; CommandManager.InvalidateRequerySuggested(); }
    }
}
