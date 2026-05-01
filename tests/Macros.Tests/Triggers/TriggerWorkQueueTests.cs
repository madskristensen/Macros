using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine.Triggers;
using Microsoft.VisualStudio.Threading;
using Xunit;

namespace Macros.Tests.Triggers;

/// <summary>
/// Unit tests for <see cref="TriggerWorkQueue"/>: empty state, ordering, seriality,
/// fault isolation, Dispose semantics, and in-flight completion on shutdown.
/// </summary>
public sealed class TriggerWorkQueueTests : IDisposable
{
    private readonly JoinableTaskContext _jtc = new();
    private JoinableTaskFactory Jtf => _jtc.Factory;

    public void Dispose() => _jtc.Dispose();

    // ── empty queue ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EmptyQueue_PendingCount_IsZero()
    {
        using var q = new TriggerWorkQueue(Jtf);
        Assert.Equal(0, q.PendingCount);
    }

    // ── ordering ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Enqueue3Items_AllRunInOrder()
    {
        using var q = new TriggerWorkQueue(Jtf);
        var results = new List<int>();

        var t1 = q.EnqueueAsync(async () => { results.Add(1); await Task.Yield(); });
        var t2 = q.EnqueueAsync(async () => { results.Add(2); await Task.Yield(); });
        var t3 = q.EnqueueAsync(async () => { results.Add(3); await Task.Yield(); });

        await Task.WhenAll(t1, t2, t3);

        Assert.Equal(new[] { 1, 2, 3 }, results);
    }

    [Fact]
    public async Task ReturnedTasks_CompleteInOrder()
    {
        using var q = new TriggerWorkQueue(Jtf);
        var completionOrder = new List<int>();

        var t1 = q.EnqueueAsync(async () => { await Task.Yield(); });
        var t2 = q.EnqueueAsync(async () => { await Task.Yield(); });
        var t3 = q.EnqueueAsync(async () => { await Task.Yield(); });

        // Serial queue: t1 finishes before t2, t2 before t3.
        await t1; completionOrder.Add(1);
        await t2; completionOrder.Add(2);
        await t3; completionOrder.Add(3);

        Assert.Equal(new[] { 1, 2, 3 }, completionOrder);
    }

    // ── seriality ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Items_RunSerially_NoConcurrentOverlap()
    {
        using var q = new TriggerWorkQueue(Jtf);
        int concurrent = 0;
        bool violation = false;

        async Task Work()
        {
            int c = Interlocked.Increment(ref concurrent);
            if (c > 1) violation = true;
            await Task.Yield();
            Interlocked.Decrement(ref concurrent);
        }

        var tasks = new Task[5];
        for (int i = 0; i < 5; i++)
            tasks[i] = q.EnqueueAsync(Work);

        await Task.WhenAll(tasks);

        Assert.False(violation, "Work items ran concurrently — seriality violated.");
        Assert.Equal(0, concurrent);
    }

    // ── fault isolation ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FailedItem_DoesNotBreakDrainLoop_NextItemStillRuns()
    {
        using var q = new TriggerWorkQueue(Jtf);
        var results = new List<int>();

        var t1 = q.EnqueueAsync(() => throw new InvalidOperationException("boom"));
        var t2 = q.EnqueueAsync(() => { results.Add(1); return Task.CompletedTask; });

        await Assert.ThrowsAsync<InvalidOperationException>(() => t1);
        await t2;  // drain loop must still process this

        Assert.Contains(1, results);
    }

    [Fact]
    public async Task FailedItem_FaultsReturnedTask_WithOriginalException()
    {
        using var q = new TriggerWorkQueue(Jtf);

        var t = q.EnqueueAsync(() => throw new ArgumentException("oops"));
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => t);

        Assert.Equal("oops", ex.Message);
    }

    // ── Dispose semantics ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dispose_SubsequentEnqueue_ThrowsObjectDisposedException()
    {
        var q = new TriggerWorkQueue(Jtf);
        q.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => q.EnqueueAsync(() => Task.CompletedTask));
    }

    [Fact]
    public void DoubleDispose_DoesNotThrow()
    {
        var q = new TriggerWorkQueue(Jtf);
        q.Dispose();
        q.Dispose(); // must be idempotent
    }

    // ── in-flight work finishes on Dispose ─────────────────────────────────────────────────

    [Fact]
    public async Task Dispose_MidExecution_InflightWorkFinishes()
    {
        var q = new TriggerWorkQueue(Jtf);

        var workStarted = new SemaphoreSlim(0);
        var workCanFinish = new SemaphoreSlim(0);
        bool workCompleted = false;

        var t = q.EnqueueAsync(async () =>
        {
            workStarted.Release();
            await workCanFinish.WaitAsync();
            workCompleted = true;
        });

        await workStarted.WaitAsync();

        q.Dispose(); // cancel drain — but in-flight item is already running

        workCanFinish.Release(); // allow the item to finish

        await t; // task must complete successfully

        Assert.True(workCompleted);
    }
}
