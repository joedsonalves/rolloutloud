using System.Windows.Input;

namespace RolloutLoud.App.Views;

/// <summary>
/// Minimal <see cref="ICommand"/> for the view model.
/// </summary>
/// <remarks>
/// Hand-rolled rather than pulled from an MVVM toolkit: the app needs a command type and nothing
/// else from one, and a dependency whose only used surface is forty lines is a dependency that
/// costs more to keep current than to write.
/// </remarks>
public sealed class RelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private bool _running;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        : this(parameter => { execute(parameter); return Task.CompletedTask; }, canExecute)
    {
    }

    public RelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        // Guard against the double-click that fires a second launch while the first is still
        // starting. Cheap here, and the alternative is two elevated terminals from one click.
        _running = true;
        RaiseCanExecuteChanged();

        try
        {
            await _execute(parameter).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // A command that throws into the void takes the whole UI down on some platforms, and
            // says nothing on others. Surface it and keep the window alive.
            Failed?.Invoke(ex);
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    /// <summary>Raised when the handler throws, so the view model can put it in the activity log.</summary>
    public static event Action<Exception>? Failed;

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
