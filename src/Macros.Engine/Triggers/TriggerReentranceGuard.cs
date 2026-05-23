using System;
using System.Collections.Generic;
using System.Threading;

namespace Macros.Engine.Triggers;

/// <summary>
/// Thread-local reentrance guard for the macro trigger pipeline.
/// Prevents a trigger from re-firing itself (directly or transitively) and caps the
/// maximum depth of trigger-induced execution at <see cref="MaxDepth"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each call to <see cref="TryEnter"/> is keyed by a string that encodes both the
/// trigger category and the command/event name:
/// <list type="bullet">
///   <item><c>"event:{canonicalName}"</c> — VS event bus firings</item>
///   <item><c>"before:{commandName}"</c> — BeforeCommand dispatch</item>
///   <item><c>"after:{commandName}"</c> — AfterCommand dispatch</item>
/// </list>
/// Using separate prefixes ensures, e.g., a BeforeCommand guard does not suppress
/// the AfterCommand guard for the same command name.
/// </para>
/// <para>
/// <b>Why thread-local instead of <see cref="AsyncLocal{T}"/>?</b> Trigger reentrance
/// typically occurs when a macro's <c>ExecuteCommandAsync</c> call dispatches a VS
/// command through COM (<c>DTE.ExecuteCommand</c> → OLE message pump →
/// <c>IOleCommandTarget.Exec</c> on the priority target). The inner <c>Exec</c>
/// callback can arrive on a fresh <see cref="ExecutionContext"/> (the OLE pump does
/// not always preserve the caller's EC), so an <see cref="AsyncLocal{T}"/>-based
/// guard sees <c>depth == 0</c> and fails to suppress the recursion — re-running the
/// macro, which re-issues the command, ad infinitum until VS freezes (see
/// <c>madskristensen/Macros#12</c>). All Before/After dispatch happens on the UI
/// thread (priority command targets and <c>CommandEvents.AfterExecute</c> are both
/// UI-thread bound), so a per-thread store reliably catches the recursion regardless
/// of EC flow across COM/JTF boundaries.
/// </para>
/// </remarks>
internal sealed class TriggerReentranceGuard
{
    /// <summary>Maximum nesting depth of trigger-induced executions allowed.</summary>
    public const int MaxDepth = 3;

    private readonly ThreadLocal<int> _depth = new(() => 0);
    private readonly ThreadLocal<HashSet<string>?> _activeKeys = new(() => null);

    /// <summary>
    /// Gets the current trigger nesting depth on the calling thread.
    /// </summary>
    public int CurrentDepth => _depth.Value;

    /// <summary>
    /// Attempts to enter a guarded scope for <paramref name="key"/>.
    /// Returns <see langword="false"/> (and sets <paramref name="scope"/> to
    /// <see langword="null"/>) when the key is already active in this async context,
    /// or when the nesting depth would exceed <see cref="MaxDepth"/>.
    /// </summary>
    /// <param name="key">The unique key identifying the trigger category + name.</param>
    /// <param name="scope">
    /// On success: a disposable that decrements the depth and removes the key when disposed.
    /// On failure: <see langword="null"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the scope was entered; <see langword="false"/> when
    /// suppressed.
    /// </returns>
    public bool TryEnter(string key, out IDisposable? scope)
    {
        scope = null;
        if (_depth.Value >= MaxDepth) return false;

        var set = _activeKeys.Value;
        if (set is not null && set.Contains(key)) return false;

        // Snapshot the previous set so the Releaser can restore it exactly, leaving
        // any parent scope's set untouched.
        var newSet = set is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(set, StringComparer.OrdinalIgnoreCase);
        newSet.Add(key);
        _activeKeys.Value = newSet;
        _depth.Value = _depth.Value + 1;
        scope = new Releaser(this, set);
        return true;
    }

    private sealed class Releaser : IDisposable
    {
        private readonly TriggerReentranceGuard _owner;
        private readonly HashSet<string>? _previousSet;
        private bool _disposed;

        public Releaser(TriggerReentranceGuard owner, HashSet<string>? previousSet)
        {
            _owner = owner;
            _previousSet = previousSet;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner._activeKeys.Value = _previousSet;
            _owner._depth.Value = Math.Max(0, _owner._depth.Value - 1);
        }
    }
}
