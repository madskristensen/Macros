using System;
using Macros.Engine.Triggers;

namespace Macros.Engine;

/// <summary>
/// Event payload raised by <see cref="IMacroService.TriggeredExecutionStarted"/>
/// and <see cref="IMacroService.TriggeredExecutionEnded"/> when a macro is executed
/// as a result of a trigger (not manual playback).
/// </summary>
public sealed class TriggeredExecutionEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TriggeredExecutionEventArgs"/> class.
    /// </summary>
    /// <param name="macroName">The friendly name of the macro being executed.</param>
    /// <param name="triggerName">The name of the trigger that caused this execution (e.g. command name).</param>
    /// <param name="kind">The kind of trigger that initiated this execution.</param>
    public TriggeredExecutionEventArgs(string macroName, string triggerName, TriggerKind kind)
    {
        if (string.IsNullOrWhiteSpace(macroName))
        {
            throw new ArgumentException("Macro name must be non-empty.", nameof(macroName));
        }

        if (string.IsNullOrWhiteSpace(triggerName))
        {
            throw new ArgumentException("Trigger name must be non-empty.", nameof(triggerName));
        }

        MacroName = macroName;
        TriggerName = triggerName;
        Kind = kind;
    }

    /// <summary>Gets the friendly name of the macro being executed.</summary>
    public string MacroName { get; }

    /// <summary>Gets the name of the trigger that caused this execution.</summary>
    public string TriggerName { get; }

    /// <summary>Gets the kind of trigger that initiated this execution.</summary>
    public TriggerKind Kind { get; }
}
