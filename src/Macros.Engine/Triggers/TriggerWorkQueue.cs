using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

namespace Macros.Engine.Triggers;

/// <summary>
/// Serial work queue for macro trigger dispatch. Enqueued items run one at a time, preventing
/// concurrent access to <c>Helpers.CurrentGlobals</c> and keeping UI responsive during dispatch.
/// </summary>
/// <remarks>
/// <para>
/// <b>Implementation choice:</b> <see cref="ConcurrentQueue{T}"/> + <see cref="SemaphoreSlim"/>
/// signalling rather than <c>System.Threading.Channels</c>, to avoid adding a NuGet dependency
/// to <c>Macros.Engine</c> (which targets net48 and keeps a minimal footprint).
/// </para>
/// <para>
/// <b>After Dispose:</b> <see cref="EnqueueAsync"/> throws <see cref="ObjectDisposedException"/>.
/// The in-flight item (if any) is allowed to finish before the drain loop exits.
/// </para>
/// </remarks>
internal sealed class TriggerWorkQueue : IDisposable
{
    private readonly ConcurrentQueue<Func<Task>> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly JoinableTaskFactory _jtf;
    private readonly CancellationTokenSource _cts = new();
    private readonly JoinableTask _drainTask;
    private volatile bool _disposed;

    public TriggerWorkQueue(JoinableTaskFactory jtf)
    {
        _jtf = jtf ?? throw new ArgumentNullException(nameof(jtf));
        _drainTask = _jtf.RunAsync(DrainAsync);
    }

    /// <summary>
    /// Enqueues <paramref name="work"/> for serial execution. Returns a <see cref="Task"/>
    /// that completes (or faults) when this specific item has run.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown after <see cref="Dispose"/>.</exception>
    public Task EnqueueAsync(Func<Task> work)
    {
        if (work == null) throw new ArgumentNullException(nameof(work));
        if (_disposed) throw new ObjectDisposedException(nameof(TriggerWorkQueue));

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        _signal.Release();
        return tcs.Task;
    }

    /// <summary>Number of items waiting to be processed.</summary>
    public int PendingCount => _queue.Count;

    private async Task DrainAsync()
    {
        while (true)
        {
            try
            {
                await _signal.WaitAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_queue.TryDequeue(out var work))
            {
                try
                {
                    await work().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Swallow — a failing item must not abort the drain loop.
                    Debug.WriteLine($"TriggerWorkQueue: work item threw: {ex}");
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
    }
}
