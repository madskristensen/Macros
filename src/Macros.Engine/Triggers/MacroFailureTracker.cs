using System;
using System.Collections.Generic;
using System.IO;

namespace Macros.Engine.Triggers;

/// <summary>
/// Default <see cref="IMacroFailureTracker"/> implementation. Thread-safe; all state mutations
/// are lock-guarded. Events fire outside the lock so re-entrant calls from subscribers are safe.
/// </summary>
/// <remarks>
/// <b>Threshold semantics</b>: a macro is auto-disabled when its consecutive-failure streak
/// reaches <c>threshold</c>. A single <see cref="RecordSuccess"/> call resets the streak AND
/// removes the macro from the disabled set, so a successful run fully clears the penalty.
/// <see cref="ReEnable"/> provides a manual override that does the same reset without requiring
/// a successful play — useful for the "Re-enable" button on the InfoBar.
/// </remarks>
internal sealed class MacroFailureTracker : IMacroFailureTracker
{
    private readonly int _threshold;
    private readonly object _sync = new();

    // path → consecutive failure count
    private readonly Dictionary<string, int> _streakByPath = new(StringComparer.OrdinalIgnoreCase);

    // paths that have reached the threshold
    private readonly HashSet<string> _disabled = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc cref="IMacroFailureTracker.AutoDisabled"/>
    public event EventHandler<MacroAutoDisabledEventArgs>? AutoDisabled;

    /// <summary>
    /// Initialises a new <see cref="MacroFailureTracker"/>.
    /// </summary>
    /// <param name="threshold">
    /// Number of consecutive failures required to auto-disable a macro. Must be ≥ 1.
    /// Defaults to 3.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="threshold"/> is less than 1.</exception>
    public MacroFailureTracker(int threshold = 3)
    {
        if (threshold < 1) throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "Threshold must be at least 1.");
        _threshold = threshold;
    }

    /// <inheritdoc />
    public void RecordSuccess(string macroPath)
    {
        if (string.IsNullOrEmpty(macroPath)) return;

        lock (_sync)
        {
            _streakByPath.Remove(macroPath);
            _disabled.Remove(macroPath);
        }
    }

    /// <inheritdoc />
    public bool RecordFailure(string macroPath)
    {
        if (string.IsNullOrEmpty(macroPath)) return false;

        MacroAutoDisabledEventArgs? eventArgs = null;

        lock (_sync)
        {
            // If already disabled, nothing more to do — just report true.
            if (_disabled.Contains(macroPath)) return true;

            _streakByPath.TryGetValue(macroPath, out var current);
            var next = current + 1;
            _streakByPath[macroPath] = next;

            if (next >= _threshold)
            {
                _disabled.Add(macroPath);
                var macroName = Path.GetFileNameWithoutExtension(macroPath);
                eventArgs = new MacroAutoDisabledEventArgs(macroPath, macroName, next);
            }
        }

        // Fire outside the lock so subscribers can safely call back into this tracker.
        if (eventArgs is not null)
        {
            AutoDisabled?.Invoke(this, eventArgs);
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public bool IsAutoDisabled(string macroPath)
    {
        if (string.IsNullOrEmpty(macroPath)) return false;
        lock (_sync) return _disabled.Contains(macroPath);
    }

    /// <inheritdoc />
    public void ReEnable(string macroPath)
    {
        if (string.IsNullOrEmpty(macroPath)) return;
        lock (_sync)
        {
            _disabled.Remove(macroPath);
            _streakByPath.Remove(macroPath);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetDisabledPaths()
    {
        lock (_sync) return new List<string>(_disabled);
    }
}
