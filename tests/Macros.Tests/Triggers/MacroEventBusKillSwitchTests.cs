using System;
using System.Collections.Generic;
using Macros.Engine.Triggers;
using Xunit;

namespace Macros.Tests.Triggers;

/// <summary>
/// Tests that verify <see cref="MacroEventBus"/> honours the kill-switch provider injected
/// via the <c>isDisabledProvider</c> constructor parameter.
/// </summary>
public sealed class MacroEventBusKillSwitchTests
{
    // --- fake event surface (mirrors MacroEventBusTests pattern) --------------------------

    public sealed class FakeEventArgs : EventArgs
    {
        public string Value { get; set; } = "";
    }

    public sealed class FakeCategory
    {
        public event EventHandler<FakeEventArgs>? Fired;
        public void Raise(FakeEventArgs args) => Fired?.Invoke(this, args);
    }

    private static (MacroEventBus bus, FakeCategory cat, KnownVsEvent known) CreateBus(
        Func<bool>? killSwitch = null)
    {
        var cat = new FakeCategory();
        var known = new KnownVsEvent(
            CanonicalName: "Fake.Fired",
            Category: "Fake",
            EventName: nameof(FakeCategory.Fired),
            EventArgsType: typeof(FakeEventArgs),
            DeclaringType: typeof(FakeCategory));

        var bus = new MacroEventBus(
            new[] { known },
            t => t == typeof(FakeCategory) ? cat : null,
            isDisabledProvider: killSwitch);

        return (bus, cat, known);
    }

    // ── kill switch OFF ────────────────────────────────────────────────────────────────────

    [Fact]
    public void KillSwitchOff_Listeners_ReceiveFirings()
    {
        bool killSwitchOn = false;
        var (bus, cat, known) = CreateBus(() => killSwitchOn);

        var received = new List<MacroEvent>();
        using (bus.Subscribe(known.CanonicalName, e => received.Add(e)))
        {
            cat.Raise(new FakeEventArgs { Value = "hello" });
        }

        Assert.Single(received);
        Assert.Equal("Fake.Fired", received[0].CanonicalName);
        bus.Dispose();
    }

    // ── kill switch ON ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void KillSwitchOn_Listeners_DoNotReceiveFirings()
    {
        bool killSwitchOn = true;
        var (bus, cat, known) = CreateBus(() => killSwitchOn);

        var received = new List<MacroEvent>();
        using (bus.Subscribe(known.CanonicalName, e => received.Add(e)))
        {
            cat.Raise(new FakeEventArgs { Value = "suppressed" });
        }

        Assert.Empty(received);
        bus.Dispose();
    }

    // ── toggle live ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ToggleKillSwitch_TakesEffectImmediately_WithoutResubscribing()
    {
        bool killSwitchOn = false;
        var (bus, cat, known) = CreateBus(() => killSwitchOn);

        var received = new List<MacroEvent>();
        using (bus.Subscribe(known.CanonicalName, e => received.Add(e)))
        {
            // Kill switch off — event should arrive.
            cat.Raise(new FakeEventArgs { Value = "a" });
            Assert.Single(received);

            // Flip on — next event should be suppressed.
            killSwitchOn = true;
            cat.Raise(new FakeEventArgs { Value = "b" });
            Assert.Single(received); // still one

            // Flip back off — event should arrive again.
            killSwitchOn = false;
            cat.Raise(new FakeEventArgs { Value = "c" });
            Assert.Equal(2, received.Count);
        }

        bus.Dispose();
    }
}
