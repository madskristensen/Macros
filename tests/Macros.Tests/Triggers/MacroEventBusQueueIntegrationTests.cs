using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine.Triggers;
using Microsoft.VisualStudio.Threading;
using Xunit;

namespace Macros.Tests.Triggers;

/// <summary>
/// Integration tests verifying that <see cref="MacroEventBus"/> dispatches listeners via
/// a <see cref="TriggerWorkQueue"/> when one is supplied, and that sequential execution is
/// maintained across multiple event firings.
/// </summary>
public sealed class MacroEventBusQueueIntegrationTests : IDisposable
{
    // ── fake event surface (shared with MacroEventBusTests) ───────────────────────────────

    public sealed class FakeEventArgs : EventArgs
    {
        public int Value { get; set; }
    }

    public sealed class FakeCategory
    {
        public event EventHandler<FakeEventArgs>? Fired;
        public void Raise(int value) => Fired?.Invoke(this, new FakeEventArgs { Value = value });
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────────────

    private readonly JoinableTaskContext _jtc = new();
    private JoinableTaskFactory Jtf => _jtc.Factory;

    private (MacroEventBus bus, FakeCategory category, TriggerWorkQueue queue) CreateBus()
    {
        var category = new FakeCategory();
        var known = new KnownVsEvent(
            CanonicalName: "Integration.Fired",
            Category: "Integration",
            EventName: nameof(FakeCategory.Fired),
            EventArgsType: typeof(FakeEventArgs),
            DeclaringType: typeof(FakeCategory));

        var queue = new TriggerWorkQueue(Jtf);
        var bus = new MacroEventBus(
            new[] { known },
            t => t == typeof(FakeCategory) ? category : null,
            queue);

        return (bus, category, queue);
    }

    public void Dispose() => _jtc.Dispose();

    // ── tests ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BusWithQueue_5Firings_ListenersRunSequentially()
    {
        var (bus, category, queue) = CreateBus();
        using (bus)
        using (queue)
        {
            int concurrent = 0;
            bool violation = false;
            var completionTasks = new List<TaskCompletionSource<bool>>();

            bus.Subscribe("Integration.Fired", _ =>
            {
                int c = Interlocked.Increment(ref concurrent);
                if (c > 1) violation = true;
                // Simulate some work duration by spinning briefly.
                Thread.SpinWait(1000);
                Interlocked.Decrement(ref concurrent);
            });

            // Fire 5 times: each firing enqueues the listener.
            for (int i = 0; i < 5; i++)
                category.Raise(i);

            // Allow time for the queue to drain all items.
            await WaitUntilAsync(() => queue.PendingCount == 0, timeoutMs: 5000);

            Assert.False(violation, "Listeners ran concurrently — queue seriality violated.");
        }
    }

    [Fact]
    public async Task BusWithQueue_5Firings_AllListenersExecute()
    {
        var (bus, category, queue) = CreateBus();
        using (bus)
        using (queue)
        {
            var received = new List<int>();
            var receivedLock = new object();

            bus.Subscribe("Integration.Fired", evt =>
            {
                var value = Assert.IsType<int>(evt.Payload["Value"]);
                lock (receivedLock) received.Add(value);
            });

            for (int i = 0; i < 5; i++)
                category.Raise(i);

            await WaitUntilAsync(() => { lock (receivedLock) return received.Count == 5; }, timeoutMs: 5000);

            Assert.Equal(5, received.Count);
        }
    }

    [Fact]
    public async Task BusWithQueue_PendingCountDropsToZero_AfterDrain()
    {
        var (bus, category, queue) = CreateBus();
        using (bus)
        using (queue)
        {
            var gate = new SemaphoreSlim(0);
            bus.Subscribe("Integration.Fired", _ => gate.Wait());

            // Fire once — listener blocks so the item occupies the drain slot.
            category.Raise(0);

            // Give the drain loop a moment to dequeue and start the item.
            await Task.Delay(50);

            // Fire 4 more while the first is still running → 4 should be pending.
            for (int i = 1; i < 5; i++)
                category.Raise(i);

            await Task.Delay(50);
            int pendingDuringDispatch = queue.PendingCount;

            // Unblock all items.
            gate.Release(5);

            await WaitUntilAsync(() => queue.PendingCount == 0, timeoutMs: 5000);

            Assert.True(pendingDuringDispatch > 0,
                $"Expected PendingCount > 0 while first item was blocked, was {pendingDuringDispatch}.");
        }
    }

    [Fact]
    public void BusWithoutQueue_StillFiresSynchronously()
    {
        var category = new FakeCategory();
        var known = new KnownVsEvent(
            CanonicalName: "Integration.Fired",
            Category: "Integration",
            EventName: nameof(FakeCategory.Fired),
            EventArgsType: typeof(FakeEventArgs),
            DeclaringType: typeof(FakeCategory));

        using var bus = new MacroEventBus(
            new[] { known },
            t => t == typeof(FakeCategory) ? category : null);
        // No queue → synchronous fallback.

        int count = 0;
        bus.Subscribe("Integration.Fired", _ => count++);
        category.Raise(0);

        Assert.Equal(1, count); // synchronous: count updated immediately
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"Condition not met within {timeoutMs} ms.");
            await Task.Delay(20);
        }
    }
}
