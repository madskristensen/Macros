using System;
using System.Collections.Generic;

namespace Macros.Engine.Triggers;

/// <summary>
/// Tracks consecutive execution failures per macro and auto-disables macros that exceed the
/// configured threshold, preventing a buggy macro from spamming on every trigger event.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wiring note for dispatchers</b> — any component that calls <see cref="Player.IMacroPlayer"/>
/// and receives a <c>MacroPlayResult</c> should call:
/// <list type="bullet">
///   <item><description><see cref="RecordSuccess"/> when <c>result.Success == true</c></description></item>
///   <item><description><see cref="RecordFailure"/> when <c>result.Success == false</c> (compile error,
///   runtime exception, or timeout)</description></item>
/// </list>
/// Example (from a future command-trigger dispatcher):
/// <code>
/// var result = await _player.PlayAsync(entry, cancellation);
/// if (result.Success)
///     _failureTracker.RecordSuccess(entry.Path);
/// else
///     _failureTracker.RecordFailure(entry.Path);
/// </code>
/// The registry's <c>FindByXxx</c> methods automatically filter out auto-disabled paths, so
/// no additional guard is needed at the call site.
/// </para>
/// </remarks>
public interface IMacroFailureTracker
{
    /// <summary>Record a successful execution (resets the consecutive-failure streak for that macro path).</summary>
    void RecordSuccess(string macroPath);

    /// <summary>
    /// Record a failure. Returns <see langword="true"/> if the macro is now AT OR ABOVE the
    /// disable threshold (i.e. it just became — or was already — auto-disabled).
    /// </summary>
    bool RecordFailure(string macroPath);

    /// <summary>
    /// <see langword="true"/> iff the macro has hit its disable threshold and hasn't been re-enabled.
    /// O(1) lookup.
    /// </summary>
    bool IsAutoDisabled(string macroPath);

    /// <summary>Manually re-enable a macro (clears its failure streak so the counter starts fresh).</summary>
    void ReEnable(string macroPath);

    /// <summary>Snapshot of currently auto-disabled macro paths.</summary>
    IReadOnlyList<string> GetDisabledPaths();

    /// <summary>
    /// Raised when a macro transitions into the auto-disabled state (exactly once per disable
    /// event). UI layers subscribe to surface an InfoBar warning.
    /// Fired outside any internal lock — subscribers may safely call back into this tracker.
    /// </summary>
    event EventHandler<MacroAutoDisabledEventArgs>? AutoDisabled;
}

/// <summary>Event data emitted by <see cref="IMacroFailureTracker.AutoDisabled"/>.</summary>
public sealed class MacroAutoDisabledEventArgs : EventArgs
{
    /// <summary>Full file-system path of the macro script.</summary>
    public string MacroPath { get; }

    /// <summary>Human-readable name of the macro (file name without extension, typically).</summary>
    public string MacroName { get; }

    /// <summary>The consecutive failure count that tripped the threshold.</summary>
    public int FailureCount { get; }

    /// <param name="path">Full file-system path.</param>
    /// <param name="name">Human-readable macro name.</param>
    /// <param name="count">Failure count at the moment of disabling.</param>
    public MacroAutoDisabledEventArgs(string path, string name, int count)
    {
        MacroPath = path;
        MacroName = name;
        FailureCount = count;
    }
}
