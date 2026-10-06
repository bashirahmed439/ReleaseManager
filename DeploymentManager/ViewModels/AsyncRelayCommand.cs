using System.Windows.Input;

namespace DeploymentManager.ViewModels;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task>? _execute;
    private readonly Func<object?, Task>? _executeWithParameter;
    private readonly Func<bool>? _canExecute;
    private readonly Func<object?, bool>? _canExecuteWithParameter;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool> canExecute)
    {
        _executeWithParameter = execute;
        _canExecuteWithParameter = canExecute;
    }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => _canExecuteWithParameter?.Invoke(parameter) ?? _canExecute?.Invoke() ?? true;

    public async void Execute(object? parameter)
    {
        if (_executeWithParameter is not null)
        {
            await _executeWithParameter(parameter);
        }
        else if (_execute is not null)
        {
            await _execute();
        }
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}