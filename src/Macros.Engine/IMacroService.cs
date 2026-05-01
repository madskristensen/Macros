using System;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine.Player;
using Macros.Engine.Recording;
using Macros.Engine.Storage;

namespace Macros.Engine;

/// <summary>
/// Public surface of the macro engine. Owns the recording / playback state machine and
/// is the single coordination point that triggers, command observers, and UI bind to.
/// </summary>
/// <remarks>
/// Implementations are expected to be process-singletons. State transitions are serialized
/// internally; callers do not need to take their own locks.
/// </remarks>
public interface IMacroService
{
    /// <summary>Gets the current lifecycle state of the engine.</summary>
    MacroState State { get; }

    /// <summary>
    /// Gets the active recording sink while the engine is in <see cref="MacroState.Recording"/>,
    /// or <see langword="null"/> otherwise. Recording observers (priority command target,
    /// text-buffer listener) push raw events through this sink; the underlying
    /// session is owned by the engine and discarded on <see cref="StopRecordingAsync"/>.
    /// </summary>
    IRecordingSink? CurrentSession { get; }

    /// <summary>
    /// Gets the C# script source of the most recently recorded or loaded macro,
    /// or <see langword="null"/> if no macro has been produced yet in this session.
    /// </summary>
    string? CurrentMacroSource { get; }

    /// <summary>
    /// Gets the friendly name attached to <see cref="CurrentMacroSource"/>. Used by
    /// playback diagnostics (Output pane heading, Error List source, InfoBar title)
    /// so failures can be attributed to a recognisable macro. <see langword="null"/>
    /// when <see cref="CurrentMacroSource"/> is also <see langword="null"/>.
    /// </summary>
    string? CurrentMacroName { get; }

    /// <summary>
    /// Gets the absolute on-disk path the persistent storage layer uses for the current
    /// macro (<c>current.csx</c>), or <see langword="null"/> when the engine was
    /// constructed without storage (in-memory only — used by unit tests). Future commands
    /// such as "Edit current macro" open this path in the VS editor.
    /// </summary>
    string? CurrentMacroPath { get; }

    /// <summary>
    /// Raised after every state transition, on the thread that performed the transition.
    /// The <see cref="MacroStateChangedEventArgs.NewState"/> matches <see cref="State"/> at
    /// the moment the event is raised.
    /// </summary>
    event EventHandler<MacroStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Raised after recording is automatically stopped because the session reached
    /// <c>MaxRecordingSteps</c>. The captured steps up to the cap are preserved and
    /// can be obtained via <see cref="StopRecordingAsync"/> — do NOT call Stop again;
    /// it has already been called internally. Subscribers should surface a UI warning.
    /// </summary>
    event EventHandler? RecordingCapReached;

    /// <summary>
    /// Raised each time a recorded step is committed to the current session's step list.
    /// The event argument is the new committed step count. Fired on whichever thread the
    /// recording observer was running on; subscribers must marshal to the UI thread themselves.
    /// </summary>
    event EventHandler<int>? RecordingStepCountChanged;

    /// <summary>
    /// Gets the <see cref="RecordingSession.MaxSteps"/> of the active recording session,
    /// or <see cref="int.MaxValue"/> if no session is active. Safe to call from any thread.
    /// </summary>
    int CurrentRecordingMaxSteps { get; }

    /// <summary>
    /// Transitions the engine from <see cref="MacroState.Idle"/> to
    /// <see cref="MacroState.Recording"/> and begins capturing input.
    /// </summary>
    /// <param name="ct">Token that cancels the transition itself (not the recording session).</param>
    /// <returns>A task that completes once recording has started.</returns>
    /// <exception cref="InvalidOperationException">The engine is not in <see cref="MacroState.Idle"/>.</exception>
    Task StartRecordingAsync(CancellationToken ct = default);

    /// <summary>
    /// Transitions the engine from <see cref="MacroState.Recording"/> back to
    /// <see cref="MacroState.Idle"/>, generates the C# script for the captured session,
    /// stores it as <see cref="CurrentMacroSource"/>, and returns it.
    /// </summary>
    /// <param name="ct">Token that cancels the stop / codegen operation.</param>
    /// <returns>The generated <c>.csx</c> source for the just-completed recording.</returns>
    /// <exception cref="InvalidOperationException">The engine is not in <see cref="MacroState.Recording"/>.</exception>
    Task<string> StopRecordingAsync(CancellationToken ct = default);

    /// <summary>
    /// Plays back <see cref="CurrentMacroSource"/>: transitions
    /// <see cref="MacroState.Idle"/> → <see cref="MacroState.Playing"/> → <see cref="MacroState.Idle"/>.
    /// </summary>
    /// <param name="ct">Token that cancels playback at the next safe point.</param>
    /// <returns>
    /// A <see cref="MacroPlayResult"/> describing the outcome of compilation and execution.
    /// Compile errors, runtime exceptions, and cancellation are returned as data —
    /// callers (e.g. the <c>Macros: Play Last</c> command) decide how to surface them.
    /// When <see cref="CurrentMacroSource"/> is <see langword="null"/>, the engine returns
    /// a synthetic failure result without entering <see cref="MacroState.Playing"/>.
    /// </returns>
    /// <exception cref="InvalidOperationException">The engine is not in <see cref="MacroState.Idle"/>.</exception>
    Task<MacroPlayResult> PlayCurrentAsync(CancellationToken ct = default);

    /// <summary>
    /// Loads a macro by name from the macro store and plays it back.
    /// Transitions <see cref="MacroState.Idle"/> → <see cref="MacroState.Playing"/> → <see cref="MacroState.Idle"/>.
    /// </summary>
    /// <param name="name">Logical name of the macro to load (without file extension).</param>
    /// <param name="ct">Token that cancels playback at the next safe point.</param>
    /// <returns>A task that completes when playback finishes (or is cancelled).</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or whitespace.</exception>
    /// <exception cref="InvalidOperationException">The engine is not in <see cref="MacroState.Idle"/>.</exception>
    Task PlayNamedAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Loads the macro identified by <paramref name="name"/> and <paramref name="scope"/>
    /// from the macro store and plays it back. Transitions
    /// <see cref="MacroState.Idle"/> → <see cref="MacroState.Playing"/> → <see cref="MacroState.Idle"/>
    /// and updates <see cref="CurrentMacroSource"/> / <see cref="CurrentMacroName"/> so a
    /// subsequent <see cref="PlayCurrentAsync"/> replays the same macro.
    /// </summary>
    /// <param name="name">
    /// Logical name of the macro to load (file stem, no extension). Validated by the
    /// underlying storage; an invalid or unknown name returns a synthetic failure result
    /// rather than throwing.
    /// </param>
    /// <param name="scope">
    /// Which scope to look the macro up in. <see cref="MacroScope.Repo"/> requires an open
    /// solution; otherwise the storage layer throws <see cref="InvalidOperationException"/>
    /// which propagates to the caller.
    /// </param>
    /// <param name="cancellation">Token that cancels playback at the next safe point.</param>
    /// <returns>
    /// A <see cref="MacroPlayResult"/> describing the outcome of compilation and execution.
    /// Compile errors, runtime exceptions, missing macros, and cancellation are returned
    /// as data — callers (commands, triggers, the tool window) decide how to surface them.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or whitespace.</exception>
    /// <exception cref="InvalidOperationException">
    /// The engine is not in <see cref="MacroState.Idle"/>, or storage refused the load
    /// because no solution is open for <see cref="MacroScope.Repo"/>.
    /// </exception>
    Task<MacroPlayResult> PlayByNameAsync(string name, MacroScope scope, CancellationToken cancellation = default);

    /// <summary>
    /// Gracefully cancels any in-flight Recording or Playing transition and returns the
    /// engine to <see cref="MacroState.Idle"/>. Safe to call when already idle (no-op).
    /// </summary>
    /// <returns>A task that completes once the engine has settled back to <see cref="MacroState.Idle"/>.</returns>
    Task CancelAsync();

    /// <summary>
    /// Cancels the currently-playing macro, if any. No-op if not in
    /// <see cref="MacroState.Playing"/> state. The cancellation is propagated through the
    /// active <see cref="System.Threading.CancellationToken"/> so the script runner aborts at
    /// the next cooperative-cancellation point; <see cref="PlayCurrentAsync"/> returns a
    /// <see cref="MacroPlayResult"/> whose <c>RuntimeError</c> is an
    /// <see cref="System.OperationCanceledException"/>.
    /// </summary>
    void CancelActivePlay();
}
