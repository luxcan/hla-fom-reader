using System;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace HLAFomReader.App.Infrastructure;

/// <summary>
/// An <see cref="ICommand"/> backed by plain delegates.
/// </summary>
/// <remarks>
/// <see cref="CanExecuteChanged"/> is chained onto <see cref="CommandManager.RequerySuggested"/> so
/// WPF re-queries the command after focus and input changes without the view model doing anything;
/// <see cref="RaiseCanExecuteChanged"/> covers the cases the command manager cannot see (a
/// collection was reloaded, a comparison finished, …).
/// </remarks>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    /// <summary>Creates a command.</summary>
    /// <param name="execute">The action to run. Required.</param>
    /// <param name="canExecute">Optional guard; when omitted the command is always enabled.</param>
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => _canExecute is null || _canExecute();

    /// <inheritdoc />
    public void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _execute();
    }

    /// <summary>Forces WPF to re-evaluate <see cref="CanExecute"/> for every bound control.</summary>
    public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
}

/// <summary>
/// A strongly typed <see cref="ICommand"/> whose parameter arrives from the binding.
/// </summary>
/// <typeparam name="T">Expected parameter type. Mismatched parameters disable the command.</typeparam>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<T?, bool>? _canExecute;

    /// <summary>Creates a command.</summary>
    /// <param name="execute">The action to run with the converted parameter. Required.</param>
    /// <param name="canExecute">Optional guard; when omitted the command is always enabled.</param>
    public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    /// <inheritdoc />
    public bool CanExecute(object? parameter)
    {
        if (!TryCoerce(parameter, out var typed)) return false;
        return _canExecute is null || _canExecute(typed);
    }

    /// <inheritdoc />
    public void Execute(object? parameter)
    {
        if (!TryCoerce(parameter, out var typed)) return;
        if (_canExecute is not null && !_canExecute(typed)) return;
        _execute(typed);
    }

    /// <summary>Forces WPF to re-evaluate <see cref="CanExecute"/> for every bound control.</summary>
    public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();

    /// <summary>
    /// Converts a loosely typed binding parameter to <typeparamref name="T"/>. A <c>null</c>
    /// parameter is legal for reference and nullable types; anything of the wrong type is rejected
    /// rather than throwing, because bindings routinely evaluate against a stale DataContext.
    /// </summary>
    private static bool TryCoerce(object? parameter, out T? value)
    {
        switch (parameter)
        {
            case T typed:
                value = typed;
                return true;
            case null:
                value = default;
                return default(T) is null;
            default:
                value = default;
                return false;
        }
    }
}

/// <summary>
/// An <see cref="ICommand"/> over an asynchronous operation. The command disables itself for the
/// duration of the run so a double-click cannot start a second parse or comparison.
/// </summary>
public sealed class AsyncRelayCommand : ObservableObject, ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _isRunning;

    /// <summary>Creates a command.</summary>
    /// <param name="execute">The asynchronous operation to run. Required.</param>
    /// <param name="canExecute">Optional guard evaluated in addition to the re-entrancy block.</param>
    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    /// <summary>True while the operation is in flight. Bindable, e.g. to a progress indicator.</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetProperty(ref _isRunning, value)) return;
            RaiseCanExecuteChanged();
        }
    }

    /// <inheritdoc />
    public bool CanExecute(object? parameter) =>
        !_isRunning && (_canExecute is null || _canExecute());

    /// <inheritdoc />
    /// <remarks>
    /// Fire-and-forget by contract — <see cref="ICommand"/> has no awaitable surface — so a failure
    /// is re-raised on the dispatcher instead of vanishing as an unobserved task exception.
    /// </remarks>
    public void Execute(object? parameter) => _ = RunGuardedAsync();

    private async Task RunGuardedAsync()
    {
        try
        {
            await ExecuteAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) throw;

            // Hand the original exception (stack intact) to the application-level handler.
            // The returned DispatcherOperation is deliberately dropped: this is the last thing the
            // failed command does, and awaiting it here would just re-throw on this dead path.
            _ = dispatcher.BeginInvoke(new Action(() => ExceptionDispatchInfo.Capture(ex).Throw()));
        }
    }

    /// <summary>
    /// Runs the operation, guarding against re-entrancy. Awaitable overload for callers that want
    /// to sequence work after the command (tests, chained view-model logic).
    /// </summary>
    public async Task ExecuteAsync()
    {
        if (!CanExecute(null)) return;

        IsRunning = true;
        try
        {
            await _execute().ConfigureAwait(true);
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>Forces WPF to re-evaluate <see cref="CanExecute"/> for every bound control.</summary>
    public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
}
