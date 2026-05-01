using System;
using System.Threading;

namespace Macros.Engine.Recording;

/// <summary>
/// Async-context re-entrancy guard that flags whether the current execution context is in the
/// middle of replaying a recorded macro. Observers consult <see cref="IsReplaying"/> to avoid
/// recording playback as fresh user input — without this guard a single play would re-record
/// itself into an infinite loop on the next replay.
/// </summary>
/// <remarks>
/// <para>
/// The guard uses <see cref="AsyncLocal{T}"/> so that the replaying state flows across
/// <see langword="await"/> boundaries and into child tasks spawned via <see cref="Task.Run"/>.
/// Nested <see cref="Enter"/> calls compose; <see cref="IsReplaying"/> stays
/// <see langword="true"/> until every entered scope has been disposed.
/// </para>
/// <para>
/// Because <see cref="AsyncLocal{T}"/> propagates a <em>copy</em> of the value into child
/// execution contexts, a child task that <see cref="Enter"/>s its own scope does not affect
/// the parent — and a child context started when no scope is active correctly sees
/// <see langword="false"/>.
/// </para>
/// </remarks>
public static class ReplayGuard
{
    private static readonly AsyncLocal<int> _depth = new();

    /// <summary>
    /// Gets a value indicating whether the current execution context is inside an
    /// <see cref="Enter"/> scope (i.e., a macro is replaying in this context).
    /// </summary>
    public static bool IsReplaying => _depth.Value > 0;

    /// <summary>
    /// Marks the current execution context as replaying for the lifetime of the returned scope.
    /// Nested <see cref="Enter"/> calls compose; <see cref="IsReplaying"/> reverts only after
    /// every scope has been disposed. Disposing more times than <see cref="Enter"/> was called
    /// is harmless — the depth saturates at zero.
    /// </summary>
    /// <returns>A disposable scope that decrements the depth when disposed.</returns>
    public static IDisposable Enter()
    {
        _depth.Value = _depth.Value + 1;
        return new Releaser();
    }

    private sealed class Releaser : IDisposable
    {
        public void Dispose() => _depth.Value = Math.Max(0, _depth.Value - 1);
    }
}
