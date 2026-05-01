using System;
using System.Windows.Input;

namespace Macros.Mvvm;

/// <summary>
/// Minimal <see cref="ICommand"/> implementation used by the M3 tool window view-models.
/// Holds an <see cref="Action{Object}"/> for execute and an optional
/// <see cref="Func{Object, Boolean}"/> for can-execute, plus a
/// <see cref="RaiseCanExecuteChanged"/> hook so subscribers can re-poll the predicate when
/// state changes (e.g. <c>IMacroService.State</c> flips Idle ↔ Playing).
/// </summary>
/// <remarks>
/// WPF binds the command's <see cref="CanExecuteChanged"/> event to button enablement and
/// re-queries the predicate every time it fires. This MVVM helper intentionally lives in
/// <c>Macros.Mvvm</c> (and not <c>Macros.ToolWindows</c>) so future commands outside the
/// tool window can re-use it without taking a tool-window dependency.
/// </remarks>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    /// <summary>
    /// Initializes a new instance of the <see cref="RelayCommand"/> class with parameter-aware
    /// delegates.
    /// </summary>
    /// <param name="execute">The action invoked when WPF calls <see cref="Execute"/>. Required.</param>
    /// <param name="canExecute">
    /// Optional predicate consulted by <see cref="CanExecute"/>. When <see langword="null"/>
    /// the command is always executable.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="execute"/> is <see langword="null"/>.</exception>
    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <summary>
    /// Convenience overload for command targets that ignore the WPF command parameter.
    /// </summary>
    /// <param name="execute">The action invoked when WPF calls <see cref="Execute"/>. Required.</param>
    /// <param name="canExecute">
    /// Optional predicate consulted by <see cref="CanExecute"/>. When <see langword="null"/>
    /// the command is always executable.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="execute"/> is <see langword="null"/>.</exception>
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(
            ToParameterized(execute),
            canExecute is null ? null : new Func<object?, bool>(_ => canExecute()))
    {
    }

    // Validate eagerly (during construction) rather than deferring to first invoke, which would
    // hide bad wiring until a user clicked the button.
    private static Action<object?> ToParameterized(Action execute)
    {
        if (execute is null)
        {
            throw new ArgumentNullException(nameof(execute));
        }

        return _ => execute();
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    /// <inheritdoc />
    public void Execute(object? parameter) => _execute(parameter);

    /// <summary>
    /// Re-raises <see cref="CanExecuteChanged"/> so WPF re-queries <see cref="CanExecute"/>.
    /// View-models call this whenever an observable state input to the predicate changes
    /// (e.g. the engine transitions to/from <c>MacroState.Idle</c>).
    /// </summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
