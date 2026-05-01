using System;
using System.Collections.Generic;

namespace Macros.Engine.Recording;

/// <summary>
/// In-memory, ordered list of <see cref="RecordedStep"/> entries captured between
/// <see cref="MacroService.StartRecordingAsync"/> and
/// <see cref="MacroService.StopRecordingAsync"/>. Owned by the <see cref="MacroService"/>;
/// observers see only the <see cref="IRecordingSink"/> facet.
/// </summary>
/// <remarks>
/// <para>
/// The session is intentionally not persisted — once <c>StopRecording</c> hands it to the
/// (future) code generator and the .csx is produced, the session object is discarded. The
/// .csx is the canonical artifact.
/// </para>
/// <para>
/// Thread-safety: every list mutation and every read of <see cref="Steps"/> takes the same
/// monitor (<c>_gate</c>). The <see cref="IsCapturing"/> check is also re-evaluated inside the
/// lock in <see cref="OnCommand"/> so a race with a concurrent state transition can't sneak
/// a step into a session that just stopped capturing.
/// </para>
/// </remarks>
internal sealed class RecordingSession : IRecordingSink, ITextEditSink
{
    private readonly MacroService _owner;
    private readonly int _maxSteps;
    private readonly object _gate = new();
    private readonly List<RecordedStep> _steps = new();
    private readonly StepAggregator _aggregator = new();
    private bool _capFired;

    /// <summary>Initializes a new <see cref="RecordingSession"/> bound to the supplied owner.</summary>
    /// <param name="owner">The <see cref="MacroService"/> that just transitioned into <see cref="MacroState.Recording"/>.</param>
    /// <param name="maxSteps">
    /// Maximum number of steps to capture before automatically stopping the recording.
    /// Defaults to <see cref="int.MaxValue"/> (no cap) so tests that don't specify a limit
    /// never trigger the cap path. Production code passes
    /// <c>MacrosOptions.Instance.MaxRecordingSteps</c> via the <see cref="MacroService"/> constructor.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="owner"/> is <see langword="null"/>.</exception>
    public RecordingSession(MacroService owner, int maxSteps = int.MaxValue)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _maxSteps = maxSteps > 0 ? maxSteps : int.MaxValue;
    }

    /// <summary>
    /// Raised at most once per session, immediately after the committed step count reaches
    /// <see cref="MaxSteps"/>. Raised outside the session lock; subscribers must not call
    /// back into <see cref="OnCommand"/> or <see cref="OnTextEdit"/> from the handler.
    /// </summary>
    public event EventHandler? CapReached;

    /// <summary>
    /// Raised each time a step is committed to the internal list (i.e. after
    /// <c>_steps.Add</c>). The argument is the new committed step count.
    /// Raised outside the session lock.
    /// </summary>
    public event EventHandler<int>? StepCountChanged;

    /// <summary>Gets the wall-clock time the session was constructed (UTC).</summary>
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets the maximum number of committed steps allowed before the session fires
    /// <see cref="CapReached"/>. <see cref="int.MaxValue"/> means no cap.
    /// </summary>
    public int MaxSteps => _maxSteps;

    /// <inheritdoc />
    public bool IsCapturing => _owner.State == MacroState.Recording && !ReplayGuard.IsReplaying;

    /// <summary>
    /// Gets a snapshot of the captured steps in arrival order, with the aggregator's currently
    /// pending (partially merged) step appended at the end if one exists. Returns a defensive
    /// copy so callers can iterate without holding the session lock.
    /// </summary>
    /// <remarks>
    /// The pending tail is included so live observers (the status bar step counter, tests) see
    /// every event the user has performed — even ones still being coalesced. The downside is
    /// that the same logical step may appear with different shape across two consecutive
    /// snapshots if a merge happened in between; that's a non-issue for the count display and
    /// the only consumers that care about exact equality call <c>DrainAndStop</c> instead.
    /// </remarks>
    public IReadOnlyList<RecordedStep> Steps
    {
        get
        {
            lock (_gate)
            {
                var pending = _aggregator.Peek();
                if (pending is null)
                {
                    return _steps.ToArray();
                }

                var copy = new RecordedStep[_steps.Count + 1];
                _steps.CopyTo(copy, 0);
                copy[_steps.Count] = pending;
                return copy;
            }
        }
    }

    /// <summary>Gets the number of steps captured so far (committed + pending). Cheap; safe to poll from any thread.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _steps.Count + (_aggregator.HasPending ? 1 : 0);
            }
        }
    }

    /// <inheritdoc />
    public void OnCommand(Guid group, uint id, string? canonicalName)
    {
        int committedCount = 0;
        bool fireCap = false;

        lock (_gate)
        {
            if (!IsCapturing || _capFired)
            {
                return;
            }

            var finalized = _aggregator.Push(
                new RecordedStep.CommandStep(group, id, canonicalName),
                DateTime.UtcNow);
            if (finalized is not null)
            {
                _steps.Add(finalized);
                committedCount = _steps.Count;

                if (_steps.Count >= _maxSteps)
                {
                    _capFired = true;
                    _aggregator.Reset(); // discard the current step now sitting in pending
                    fireCap = true;
                }
            }
        }

        if (committedCount > 0)
        {
            StepCountChanged?.Invoke(this, committedCount);
        }

        if (fireCap)
        {
            CapReached?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public void OnFileOpen(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        int committedCount = 0;
        bool fireCap = false;

        lock (_gate)
        {
            if (!IsCapturing || _capFired)
            {
                return;
            }

            var finalized = _aggregator.Push(
                new RecordedStep.FileOpenStep(path),
                DateTime.UtcNow);
            if (finalized is not null)
            {
                _steps.Add(finalized);
                committedCount = _steps.Count;

                if (_steps.Count >= _maxSteps)
                {
                    _capFired = true;
                    _aggregator.Reset();
                    fireCap = true;
                }
            }
        }

        if (committedCount > 0)
        {
            StepCountChanged?.Invoke(this, committedCount);
        }

        if (fireCap)
        {
            CapReached?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public void OnTextEdit(TextEditStep step)
    {
        if (step is null)
        {
            throw new ArgumentNullException(nameof(step));
        }

        int committedCount = 0;
        bool fireCap = false;

        lock (_gate)
        {
            if (!IsCapturing || _capFired)
            {
                return;
            }

            var finalized = _aggregator.Push(step, DateTime.UtcNow);
            if (finalized is not null)
            {
                _steps.Add(finalized);
                committedCount = _steps.Count;

                if (_steps.Count >= _maxSteps)
                {
                    _capFired = true;
                    _aggregator.Reset();
                    fireCap = true;
                }
            }
        }

        if (committedCount > 0)
        {
            StepCountChanged?.Invoke(this, committedCount);
        }

        if (fireCap)
        {
            CapReached?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Drains the aggregator's pending slot into the committed list and returns a defensive
    /// snapshot of every recorded step, in arrival order. Engine-internal: invoked by
    /// <see cref="MacroService.StopRecordingAsync"/> after the state transition flips
    /// <see cref="IsCapturing"/> to <see langword="false"/>, so no concurrent observer push
    /// can race with the drain.
    /// </summary>
    /// <remarks>
    /// The session's internal <c>_steps</c> list is intentionally NOT cleared. The session
    /// object is discarded by <see cref="MacroService.StopRecordingAsync"/> immediately after
    /// this call (its <see cref="IRecordingSink"/> reference is set to <see langword="null"/>),
    /// so clearing is unnecessary and would only obscure post-mortem inspection in the debugger.
    /// </remarks>
    /// <returns>An immutable snapshot of every coalesced step captured during the session.</returns>
    internal IReadOnlyList<RecordedStep> DrainAndStop()
    {
        lock (_gate)
        {
            var pending = _aggregator.Flush();
            if (pending is not null)
            {
                _steps.Add(pending);
            }

            return _steps.ToArray();
        }
    }
}
