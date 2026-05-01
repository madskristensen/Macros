using System;

namespace Macros.Engine;

/// <summary>
/// Event payload raised by <see cref="IMacroService.StateChanged"/> after the engine
/// commits a state transition.
/// </summary>
/// <remarks>
/// The event is raised <em>after</em> <see cref="IMacroService.State"/> has been updated to
/// <see cref="NewState"/>, so handlers can read the service's current state and observe the
/// same value as <see cref="NewState"/>.
/// </remarks>
public sealed class MacroStateChangedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MacroStateChangedEventArgs"/> class.
    /// </summary>
    /// <param name="oldState">The state the engine just left.</param>
    /// <param name="newState">The state the engine just entered.</param>
    public MacroStateChangedEventArgs(MacroState oldState, MacroState newState)
    {
        OldState = oldState;
        NewState = newState;
    }

    /// <summary>Gets the state the engine was in immediately before the transition.</summary>
    public MacroState OldState { get; }

    /// <summary>Gets the state the engine is in immediately after the transition.</summary>
    public MacroState NewState { get; }
}
