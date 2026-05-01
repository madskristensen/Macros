using System;

namespace Macros.Engine.Recording;

/// <summary>
/// Coalesces consecutive <see cref="RecordedStep"/> events into the smallest set of logical
/// steps the code generator can emit. The most common case is folding a run of single-character
/// <see cref="TextEditStep"/> insertions ("typed t-h-e-n") into one combined insertion the
/// generator renders as a single <c>Type("...")</c> call.
/// </summary>
/// <remarks>
/// <para>
/// The aggregator owns a single <em>pending</em> slot. Each <see cref="Push"/> tries to merge
/// the incoming event into <c>_pending</c>; if that fails, the previous <c>_pending</c> is
/// returned to the caller (which appends it to its committed list) and the incoming event
/// becomes the new <c>_pending</c>. When recording stops, the caller MUST drain the last
/// pending event with <see cref="Flush"/>.
/// </para>
/// <para>
/// The class is intentionally <em>not</em> thread-safe — <see cref="RecordingSession"/> already
/// serialises every observer push under its own monitor, so adding a second lock here would
/// just nest pointlessly. Tests can call into it on a single thread directly.
/// </para>
/// <para>
/// Merge rules (mirrored in <c>danny-engine-architecture.md</c>):
/// <list type="number">
///   <item>Two adjacent insertions (both <c>OldLength == 0 &amp;&amp; OldText == ""</c>) where
///   the new step's <c>OldPosition</c> equals <c>previous.OldPosition + previous.NewText.Length</c>
///   merge into one extended insertion.</item>
///   <item>Two adjacent backspaces (both <c>NewText == "" &amp;&amp; OldLength &gt; 0</c>) where
///   <c>new.OldPosition + new.OldLength == previous.OldPosition</c> merge into one extended deletion
///   that removes <c>new.OldText + previous.OldText</c> at <c>new.OldPosition</c>.</item>
///   <item>Two adjacent forward-deletes (both deletions) where
///   <c>new.OldPosition == previous.OldPosition</c> merge into one extended deletion that removes
///   <c>previous.OldText + new.OldText</c> at <c>previous.OldPosition</c>.</item>
///   <item><see cref="RecordedStep.CommandStep"/> NEVER merges with anything; it always flushes
///   the pending slot first and then occupies the slot itself.</item>
///   <item>If the wall-clock gap between the previous merge and the new event exceeds
///   <c>flushAfter</c> (default 500ms), the pending slot is flushed even if the events would
///   otherwise be mergeable. This keeps "type a word, get coffee, type another word" from
///   collapsing into a single ugly call in the generated script.</item>
/// </list>
/// </para>
/// </remarks>
internal sealed class StepAggregator
{
    private readonly TimeSpan _flushAfter;
    private RecordedStep? _pending;
    private DateTime _pendingAtUtc;

    /// <summary>
    /// Initializes a new <see cref="StepAggregator"/>.
    /// </summary>
    /// <param name="flushAfter">
    /// Maximum wall-clock gap allowed between two events before the pending slot is force-flushed.
    /// Defaults to 500ms when <see langword="null"/>. The engine intentionally hard-codes this
    /// rather than reaching into <c>MacrosOptions</c> — <c>Macros.Engine</c> has no reference
    /// to the VSIX project. <c>m3-options-page</c> can wire the value through the constructor
    /// when the option becomes user-tunable.
    /// </param>
    public StepAggregator(TimeSpan? flushAfter = null)
    {
        _flushAfter = flushAfter ?? TimeSpan.FromMilliseconds(500);
    }

    /// <summary>
    /// Gets a value indicating whether a partially-merged step is currently buffered. Exposed
    /// for tests; production code uses <see cref="Push"/> / <see cref="Flush"/> exclusively.
    /// </summary>
    public bool HasPending => _pending is not null;

    /// <summary>
    /// Returns the current pending step without removing it from the buffer. Used by
    /// <see cref="RecordingSession"/> so live <c>Steps</c> / <c>Count</c> snapshots reflect the
    /// in-flight merge in addition to the committed list. Returns <see langword="null"/> when
    /// nothing is pending.
    /// </summary>
    public RecordedStep? Peek() => _pending;

    /// <summary>
    /// Offers <paramref name="step"/> to the aggregator. If it merges into the pending slot,
    /// returns <see langword="null"/> (caller appends nothing). Otherwise, the previous pending
    /// step is returned (caller appends it to its committed list) and <paramref name="step"/>
    /// becomes the new pending.
    /// </summary>
    /// <param name="step">The freshly observed event. Must not be <see langword="null"/>.</param>
    /// <param name="utcNow">
    /// The wall-clock time the event was observed. Passed in (rather than read from
    /// <see cref="DateTime.UtcNow"/>) so tests can drive the time-gap rule deterministically.
    /// </param>
    /// <returns>
    /// The previously-pending step if it was just flushed by this push, or <see langword="null"/>
    /// if the new event was merged into pending (or pending was empty and the new event simply
    /// became the new pending).
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="step"/> is <see langword="null"/>.</exception>
    public RecordedStep? Push(RecordedStep step, DateTime utcNow)
    {
        if (step is null)
        {
            throw new ArgumentNullException(nameof(step));
        }

        if (_pending is null)
        {
            _pending = step;
            _pendingAtUtc = utcNow;
            return null;
        }

        if (utcNow - _pendingAtUtc <= _flushAfter
            && step is TextEditStep incoming
            && _pending is TextEditStep previous
            && TryMerge(previous, incoming, out var merged))
        {
            _pending = merged;
            _pendingAtUtc = utcNow;
            return null;
        }

        var flushed = _pending;
        _pending = step;
        _pendingAtUtc = utcNow;
        return flushed;
    }

    /// <summary>
    /// Returns and clears the pending slot. Callers MUST invoke this when recording stops to
    /// avoid losing the last (un-flushed) merge.
    /// </summary>
    /// <returns>The pending step, or <see langword="null"/> if none was buffered.</returns>
    public RecordedStep? Flush()
    {
        var pending = _pending;
        _pending = null;
        _pendingAtUtc = default;
        return pending;
    }

    /// <summary>
    /// Drops the pending slot without returning it. Used when a recording session is cancelled
    /// rather than stopped — there's no committed list to drain into.
    /// </summary>
    public void Reset()
    {
        _pending = null;
        _pendingAtUtc = default;
    }

    private static bool TryMerge(TextEditStep previous, TextEditStep incoming, out TextEditStep merged)
    {
        // Rule 1: adjacent insertions ("typing forward").
        if (previous.OldLength == 0 && previous.OldText.Length == 0
            && incoming.OldLength == 0 && incoming.OldText.Length == 0
            && incoming.OldPosition == previous.OldPosition + previous.NewText.Length)
        {
            merged = new TextEditStep(
                OldPosition: previous.OldPosition,
                OldLength: 0,
                OldText: string.Empty,
                NewText: previous.NewText + incoming.NewText);
            return true;
        }

        // Rules 2 & 3: only merge when both events are pure deletions.
        if (previous.NewText.Length == 0 && previous.OldLength > 0
            && incoming.NewText.Length == 0 && incoming.OldLength > 0)
        {
            // Rule 2: backspace — the cursor sits at the end of `previous`'s removed range and
            // each new keystroke chews backwards. The combined range starts at `incoming`.
            if (incoming.OldPosition + incoming.OldLength == previous.OldPosition)
            {
                merged = new TextEditStep(
                    OldPosition: incoming.OldPosition,
                    OldLength: previous.OldLength + incoming.OldLength,
                    OldText: incoming.OldText + previous.OldText,
                    NewText: string.Empty);
                return true;
            }

            // Rule 3: forward Delete — the cursor stays put and successive keystrokes consume
            // characters to the right. Both events report the same OldPosition.
            if (incoming.OldPosition == previous.OldPosition)
            {
                merged = new TextEditStep(
                    OldPosition: previous.OldPosition,
                    OldLength: previous.OldLength + incoming.OldLength,
                    OldText: previous.OldText + incoming.OldText,
                    NewText: string.Empty);
                return true;
            }
        }

        merged = null!;
        return false;
    }
}
