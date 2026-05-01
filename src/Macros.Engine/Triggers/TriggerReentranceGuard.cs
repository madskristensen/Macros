using System;
using System.Collections.Generic;
using System.Threading;

namespace Macros.Engine.Triggers;

/// <summary>
/// AsyncLocal-based reentrance guard for the macro trigger pipeline.
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
/// <see cref="AsyncLocal{T}"/> propagates a <em>copy</em> of the state into child
/// execution contexts, so a child task that enters its own scopes cannot affect the
/// parent, and the depth/key-set naturally tracks per-async-chain reentrance.
/// </para>
/// </remarks>
internal sealed class TriggerReentranceGuard
{
    /// <summary>Maximum nesting depth of trigger-induced executions allowed.</summary>
    public const int MaxDepth = 3;

    private readonly AsyncLocal<int> _depth = new();
    private readonly AsyncLocal<HashSet<string>?> _activeKeys = new();

    /// <summary>
    /// Gets the current trigger nesting depth in this async context.
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

        var set = _activeKeys.Value ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (set.Contains(key)) return false;

        // Snapshot the previous set so the Releaser can restore it exactly, keeping
        // parent-context sets untouched (AsyncLocal copy-on-write semantics).
        var newSet = new HashSet<string>(set, StringComparer.OrdinalIgnoreCase) { key };
        _activeKeys.Value = newSet;
        _depth.Value++;
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
