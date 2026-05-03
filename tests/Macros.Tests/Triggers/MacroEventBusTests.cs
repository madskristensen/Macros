using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Macros.Engine.Triggers;
using Xunit;

namespace Macros.Tests.Triggers;

/// <summary>
/// Behavioural tests for <see cref="MacroEventBus"/>. The bus is exercised against a fake
/// event-category type (<see cref="FakeEventCategory"/>) injected via the test-only
/// constructor, so we never touch <c>Community.VisualStudio.Toolkit.VS.Events</c> from a
/// unit-test process.
/// </summary>
public sealed class MacroEventBusTests
{
    // --- fake event surface ---------------------------------------------------------------

    public sealed class FakeEventArgs : EventArgs
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
    }

    public sealed class FakeEventCategory
    {
        public event EventHandler<FakeEventArgs>? Fired;
        public void RaiseFired(FakeEventArgs args) => Fired?.Invoke(this, args);
    }

    public sealed class OtherEventCategory
    {
        public event EventHandler? Pinged;
        public void RaisePinged() => Pinged?.Invoke(this, EventArgs.Empty);
    }

    private static KnownVsEvent CreateFakeFiredKnownEvent()
        => new(
            CanonicalName: "Fake.Fired",
            Category: "Fake",
            EventName: nameof(FakeEventCategory.Fired),
            EventArgsType: typeof(FakeEventArgs),
            DeclaringType: typeof(FakeEventCategory));

    private static (MacroEventBus bus, FakeEventCategory category, KnownVsEvent known)
        CreateBusWithFakeFiredEvent()
    {
        var category = new FakeEventCategory();
        var known = CreateFakeFiredKnownEvent();

        var bus = new MacroEventBus(
            new[] { known },
            t => t == typeof(FakeEventCategory) ? category : null);

        return (bus, category, known);
    }

    // --- construction / disposal ----------------------------------------------------------

    [Fact]
    public void Construct_AndDispose_NoSubscribers_DoesNotThrow()
    {
        var bus = new MacroEventBus();
        bus.Dispose();
        // Double-dispose is also a no-op.
        bus.Dispose();
    }

    [Fact]
    public void GetKnownEvents_ReturnsInjectedCatalog()
    {
        var (bus, _, known) = CreateBusWithFakeFiredEvent();
        using (bus)
        {
            var list = bus.GetKnownEvents();
            Assert.Single(list);
            Assert.Same(known, list[0]);
        }
    }

    // --- unknown-event tolerance ----------------------------------------------------------

    [Fact]
    public void Subscribe_UnknownEventName_ReturnsNoOpDisposable()
    {
        using var bus = new MacroEventBus(
            Array.Empty<KnownVsEvent>(),
            _ => null);

        var token = bus.Subscribe("Bogus.Event_xyz", _ => { });
        Assert.NotNull(token);
        // Must not throw — the contract is graceful degradation, not exceptions.
        token.Dispose();
        token.Dispose();
    }

    [Fact]
    public void Subscribe_NullName_Throws()
    {
        using var bus = new MacroEventBus(Array.Empty<KnownVsEvent>(), _ => null);
        Assert.Throws<ArgumentNullException>(() => bus.Subscribe(null!, _ => { }));
    }

    [Fact]
    public void Subscribe_NullHandler_Throws()
    {
        using var bus = new MacroEventBus(Array.Empty<KnownVsEvent>(), _ => null);
        Assert.Throws<ArgumentNullException>(() => bus.Subscribe("Foo.Bar", null!));
    }

    // --- multiple-subscriber dispatch -----------------------------------------------------

    [Fact]
    public void TwoSubscribers_BothReceiveFirings()
    {
        var (bus, category, _) = CreateBusWithFakeFiredEvent();
        using (bus)
        {
            var receivedA = new List<MacroEvent>();
            var receivedB = new List<MacroEvent>();

            using (bus.Subscribe("Fake.Fired", e => receivedA.Add(e)))
            using (bus.Subscribe("Fake.Fired", e => receivedB.Add(e)))
            {
                category.RaiseFired(new FakeEventArgs { Name = "alpha", Count = 7 });

                Assert.Single(receivedA);
                Assert.Single(receivedB);
                Assert.Equal("Fake.Fired", receivedA[0].CanonicalName);
                Assert.Equal("alpha", receivedA[0].Payload["Name"]);
                Assert.Equal(7, receivedA[0].Payload["Count"]);
                Assert.Equal("alpha", receivedB[0].Payload["Name"]);
            }
        }
    }

    [Fact]
    public void Subscribe_IsCaseInsensitive()
    {
        var (bus, category, _) = CreateBusWithFakeFiredEvent();
        using (bus)
        {
            var received = new List<MacroEvent>();
            using (bus.Subscribe("fake.FIRED", e => received.Add(e)))
            {
                category.RaiseFired(new FakeEventArgs { Name = "x" });
                Assert.Single(received);
            }
        }
    }

    // --- lazy-subscribe / refcount lifecycle ----------------------------------------------

    [Fact]
    public void FirstSubscriber_AttachesUnderlyingHandler()
    {
        var (bus, _, _) = CreateBusWithFakeFiredEvent();
        using (bus)
        {
            Assert.Null(GetAttachedHandler(bus, "Fake.Fired"));

            using (bus.Subscribe("Fake.Fired", _ => { }))
            {
                Assert.NotNull(GetAttachedHandler(bus, "Fake.Fired"));
            }
        }
    }

    [Fact]
    public void LastSubscriberDisposes_DetachesUnderlyingHandler()
    {
        var (bus, category, _) = CreateBusWithFakeFiredEvent();
        using (bus)
        {
            var sub1 = bus.Subscribe("Fake.Fired", _ => { });
            var sub2 = bus.Subscribe("Fake.Fired", _ => { });

            // While listeners exist, the VS-side handler stays attached.
            Assert.NotNull(GetAttachedHandler(bus, "Fake.Fired"));

            sub1.Dispose();
            // Still has one listener; handler must remain attached.
            Assert.NotNull(GetAttachedHandler(bus, "Fake.Fired"));

            sub2.Dispose();
            // Last subscriber gone — AttachedHandler must be null.
            Assert.Null(GetAttachedHandler(bus, "Fake.Fired"));

            // Firing now must reach nobody (the FakeEventCategory has no live handler).
            var fired = false;
            category.Fired += (_, _) => fired = true; // sanity probe — separate from bus
            category.RaiseFired(new FakeEventArgs());
            Assert.True(fired); // probe ran, but no MacroEvent path remained
        }
    }

    [Fact]
    public void Resubscribe_AfterFullUnsubscribe_ReattachesUnderlyingHandler()
    {
        var (bus, category, _) = CreateBusWithFakeFiredEvent();
        using (bus)
        {
            var received = new List<MacroEvent>();

            var first = bus.Subscribe("Fake.Fired", e => received.Add(e));
            first.Dispose();
            Assert.Null(GetAttachedHandler(bus, "Fake.Fired"));

            using (bus.Subscribe("Fake.Fired", e => received.Add(e)))
            {
                Assert.NotNull(GetAttachedHandler(bus, "Fake.Fired"));
                category.RaiseFired(new FakeEventArgs { Name = "again" });
                Assert.Single(received);
                Assert.Equal("again", received[0].Payload["Name"]);
            }
        }
    }

    [Fact]
    public void DisposingTokenTwice_IsNoOp()
    {
        var (bus, _, _) = CreateBusWithFakeFiredEvent();
        using (bus)
        {
            using var sentinel = bus.Subscribe("Fake.Fired", _ => { });
            var token = bus.Subscribe("Fake.Fired", _ => { });
            token.Dispose();
            token.Dispose();
            // Sentinel still alive → handler must remain attached.
            Assert.NotNull(GetAttachedHandler(bus, "Fake.Fired"));
        }
    }

    [Fact]
    public void CategoryResolution_IsCachedAcrossResubscribe()
    {
        var category = new FakeEventCategory();
        var known = CreateFakeFiredKnownEvent();
        var resolveCalls = 0;
        using var bus = new MacroEventBus(
            new[] { known },
            t =>
            {
                if (t != typeof(FakeEventCategory))
                {
                    return null;
                }

                resolveCalls++;
                return category;
            });

        var first = bus.Subscribe("Fake.Fired", _ => { });
        first.Dispose();

        using (bus.Subscribe("Fake.Fired", _ => { }))
        {
            Assert.NotNull(GetAttachedHandler(bus, "Fake.Fired"));
        }

        Assert.Equal(1, resolveCalls);
    }

    [Fact]
    public void CachedInstance_AllowsDetachWhenResolverStopsResolving()
    {
        var category = new FakeEventCategory();
        var known = CreateFakeFiredKnownEvent();
        var resolveCalls = 0;
        using var bus = new MacroEventBus(
            new[] { known },
            t =>
            {
                if (t != typeof(FakeEventCategory))
                {
                    return null;
                }

                resolveCalls++;
                return resolveCalls == 1 ? category : null;
            });

        var sub = bus.Subscribe("Fake.Fired", _ => { });
        Assert.Equal(1, GetEventSubscriberCount(category, nameof(FakeEventCategory.Fired)));

        sub.Dispose();

        Assert.Equal(0, GetEventSubscriberCount(category, nameof(FakeEventCategory.Fired)));
        Assert.Equal(1, resolveCalls);
    }

    [Fact]
    public void PrewarmCategoryInstances_SeedsCacheBeforeFirstSubscribe()
    {
        var category = new FakeEventCategory();
        var known = CreateFakeFiredKnownEvent();
        var resolveCalls = 0;
        using var bus = new MacroEventBus(
            new[] { known },
            t =>
            {
                if (t != typeof(FakeEventCategory))
                {
                    return null;
                }

                resolveCalls++;
                return category;
            },
            prewarmCategoryInstances: true);

        Assert.Equal(1, resolveCalls);

        using (bus.Subscribe("Fake.Fired", _ => { }))
        {
            Assert.Equal(1, resolveCalls);
        }
    }

    // --- listener fault isolation ---------------------------------------------------------

    [Fact]
    public void ListenerException_DoesNotBreakOtherListeners()
    {
        var (bus, category, _) = CreateBusWithFakeFiredEvent();
        using (bus)
        {
            var second = new List<MacroEvent>();

            using (bus.Subscribe("Fake.Fired", _ => throw new InvalidOperationException("boom")))
            using (bus.Subscribe("Fake.Fired", e => second.Add(e)))
            {
                category.RaiseFired(new FakeEventArgs { Name = "ok" });
                Assert.Single(second);
                Assert.Equal("ok", second[0].Payload["Name"]);
            }
        }
    }

    // --- payload extraction ---------------------------------------------------------------

    [Fact]
    public void ExtractPayload_NullArgs_ReturnsEmpty()
    {
        var dict = InvokeExtractPayload(null);
        Assert.Empty(dict);
    }

    [Fact]
    public void ExtractPayload_FlatPocoEventArgs_ReturnsPropertyDictionary()
    {
        var args = new FakeEventArgs { Name = "doc.cs", Count = 3 };
        var dict = InvokeExtractPayload(args);

        Assert.Equal(2, dict.Count);
        Assert.Equal("doc.cs", dict["Name"]);
        Assert.Equal(3, dict["Count"]);
    }

    [Fact]
    public void ExtractPayload_KeysAreCaseInsensitive()
    {
        var args = new FakeEventArgs { Name = "abc" };
        var dict = InvokeExtractPayload(args);
        Assert.Equal("abc", dict["NAME"]);
        Assert.Equal("abc", dict["name"]);
    }

    [Fact]
    public void ExtractPayload_PlainEventArgs_ReturnsEmpty()
    {
        // System.EventArgs has no public instance properties — payload is the empty dictionary.
        var dict = InvokeExtractPayload(EventArgs.Empty);
        Assert.Empty(dict);
    }

    // --- bus disposal cleanup -------------------------------------------------------------

    [Fact]
    public void Dispose_WhileSubscribersExist_DetachesAndStopsDispatch()
    {
        var (bus, category, _) = CreateBusWithFakeFiredEvent();

        var received = new List<MacroEvent>();
        // Intentionally keep token undisposed — Dispose must still tear the VS-side hookup down.
        bus.Subscribe("Fake.Fired", e => received.Add(e));

        bus.Dispose();

        // Firing after Dispose — no listener should be invoked.
        category.RaiseFired(new FakeEventArgs { Name = "post-dispose" });
        Assert.Empty(received);

        // Subsequent Subscribe calls return no-op tokens, not throwing.
        var token = bus.Subscribe("Fake.Fired", _ => Assert.Fail("must not fire on disposed bus"));
        token.Dispose();
    }

    // --- reflection helpers ---------------------------------------------------------------

    private static Delegate? GetAttachedHandler(MacroEventBus bus, string canonicalName)
    {
        var entriesField = typeof(MacroEventBus).GetField(
            "_entries", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(entriesField);
        var entries = entriesField!.GetValue(bus);
        Assert.NotNull(entries);

        // Dictionary<string, BusEntry> — iterate via IDictionary projection.
        var dictType = entries!.GetType();
        var indexer = dictType.GetProperty("Item");
        var keys = (System.Collections.IEnumerable)dictType.GetProperty("Keys")!.GetValue(entries)!;

        foreach (var key in keys)
        {
            if (!string.Equals((string)key, canonicalName, StringComparison.OrdinalIgnoreCase))
                continue;

            var entry = indexer!.GetValue(entries, new object[] { key });
            var attachedProp = entry!.GetType().GetProperty(
                "AttachedHandler", BindingFlags.Public | BindingFlags.Instance);
            return (Delegate?)attachedProp!.GetValue(entry);
        }

        return null;
    }

    private static int GetEventSubscriberCount(object target, string eventName)
    {
        var field = target.GetType().GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return ((Delegate?)field!.GetValue(target))?.GetInvocationList().Length ?? 0;
    }

    private static IReadOnlyDictionary<string, object?> InvokeExtractPayload(object? eventArgs)
    {
        var method = typeof(MacroEventBus).GetMethod(
            "ExtractPayload", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (IReadOnlyDictionary<string, object?>)method!.Invoke(null, new[] { eventArgs })!;
    }
}
