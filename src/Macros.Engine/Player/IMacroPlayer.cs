using System;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine.Triggers;

namespace Macros.Engine.Player;

/// <summary>
/// Executes a <c>.csx</c> macro source against a Roslyn <c>CSharpScript</c> host with a
/// <see cref="Macros.Engine.Scripting.MacroGlobals"/> instance injected. Implementations
/// own the compile / cache / run pipeline; callers (the engine, command handlers, triggers)
/// supply only the source plus invocation metadata.
/// </summary>
/// <remarks>
/// <para>
/// Compilation errors are returned as data, not exceptions: the caller decides how to
/// surface them (M2 routes to the Output pane, Error List, and an InfoBar — see the
/// <c>m2-error-surfacing</c> todo). Runtime exceptions thrown by user code propagate the
/// same way through <see cref="MacroPlayResult.RuntimeError"/>; cancellation is reported
/// distinctly so the caller can choose to swallow it (Esc-to-cancel) rather than treat it
/// as a failure.
/// </para>
/// </remarks>
public interface IMacroPlayer
{
    /// <summary>
    /// Compiles (or re-uses a cached compilation of) <paramref name="source"/> and runs it
    /// with <c>Globals = new MacroGlobals(dte, new MacroContext(...))</c>.
    /// </summary>
    /// <param name="source">The C# script source. May be empty; never <see langword="null"/>.</param>
    /// <param name="macroName">Logical macro name surfaced via <c>Context.MacroName</c>.</param>
    /// <param name="trigger">
    /// The typed trigger that caused playback. Pass <see cref="ManualMacroTrigger.Instance"/>
    /// for user-initiated runs; <see langword="null"/> is normalised to
    /// <see cref="ManualMacroTrigger.Instance"/>.
    /// </param>
    /// <param name="cancellation">
    /// Token forwarded to the script via <c>Context.Cancellation</c> and observed by the
    /// player itself. A pre-cancelled token results in
    /// <see cref="MacroPlayResult"/>.<see cref="MacroPlayResult.Success"/> = <see langword="false"/>
    /// with <see cref="MacroPlayResult.RuntimeError"/> set to an <see cref="OperationCanceledException"/>.
    /// </param>
    /// <returns>A <see cref="MacroPlayResult"/> describing success, compile error, or runtime error.</returns>
    Task<MacroPlayResult> PlayAsync(
        string source,
        string macroName,
        IMacroTrigger? trigger,
        CancellationToken cancellation,
        string? csxFilePath = null);
}

/// <summary>
/// Outcome of a single <see cref="IMacroPlayer.PlayAsync"/> invocation.
/// </summary>
/// <param name="Success">
/// <see langword="true"/> if compilation and execution both completed without error and
/// without cancellation; <see langword="false"/> otherwise.
/// </param>
/// <param name="CompilationError">
/// Newline-joined diagnostic text when Roslyn rejected the source; <see langword="null"/>
/// when no compile-time diagnostics were emitted.
/// </param>
/// <param name="RuntimeError">
/// The exception raised inside <c>script.RunAsync</c> (including
/// <see cref="OperationCanceledException"/> for cancellation), or <see langword="null"/>
/// if the script ran to completion.
/// </param>
/// <param name="Duration">Wall-clock time from the start of <see cref="IMacroPlayer.PlayAsync"/> to its return.</param>
public sealed record MacroPlayResult(
    bool Success,
    string? CompilationError,
    Exception? RuntimeError,
    TimeSpan Duration);
