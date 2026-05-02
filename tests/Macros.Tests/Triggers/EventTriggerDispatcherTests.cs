using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine.Player;
using Macros.Engine.Storage;
using Macros.Engine.Triggers;
using Macros.Triggers;
using Microsoft.VisualStudio.Threading;
using Xunit;

namespace Macros.Tests.Triggers;

/// <summary>
/// Pins the bus → registry → player wiring delivered by
/// <see cref="EventTriggerDispatcher"/> — the bridge that makes
/// <c>// @trigger Build.SolutionBuildDone</c> actually run a macro when the underlying VS
/// event fires. Uses the bus's test-only constructor with a hand-rolled fake event
/// category so the wiring can be exercised without the toolkit.
/// </summary>
public sealed class EventTriggerDispatcherTests
{
    private const string CanonicalName = "Fake.Fired";

    public sealed class FakeEventArgs : EventArgs
    {
        public string Name { get; set; } = "";
    }

    public sealed class FakeEventCategory
    {
        public event EventHandler<FakeEventArgs>? Fired;
        public void RaiseFired(FakeEventArgs args) => Fired?.Invoke(this, args);
    }

    private static JoinableTaskFactory CreateJtf()
    {
#pragma warning disable VSSDK005
        return new JoinableTaskContext().Factory;
#pragma warning restore VSSDK005
    }

    private static (MacroEventBus bus, FakeEventCategory cat) CreateBus()
    {
        var cat = new FakeEventCategory();
        var known = new KnownVsEvent(
            CanonicalName: CanonicalName,
            Category: "Fake",
            EventName: nameof(FakeEventCategory.Fired),
            EventArgsType: typeof(FakeEventArgs),
            DeclaringType: typeof(FakeEventCategory));
        var bus = new MacroEventBus(new[] { known }, _ => cat);
        return (bus, cat);
    }

    private static MacroEntry MakeEntry(string name, string canonicalEventName)
        => new(
            name,
            MacroScope.Global,
            $"X:\\fake\\{name}.csx",
            StepCount: 0,
            Modified: DateTimeOffset.UtcNow,
            SizeBytes: 0,
            Triggers: new[] { new TriggerBinding(TriggerKind.VsEvent, canonicalEventName) });

    [Fact]
    public async Task BusFires_RegisteredMacroPlayed()
    {
        var (bus, cat) = CreateBus();
        var registry = new FakeRegistry();
        var entry = MakeEntry("BuildLogger", CanonicalName);
        registry.EventMatches[CanonicalName] = new List<TriggerMatch>
        {
            new(entry, new TriggerBinding(TriggerKind.VsEvent, CanonicalName)),
        };
        var player = new FakePlayer();
        var jtf = CreateJtf();
        using var dispatcher = new EventTriggerDispatcher(
            registry, bus, player, jtf,
            sourceLoader: _ => "// source");

        cat.RaiseFired(new FakeEventArgs { Name = "Solution.sln" });

        await WaitForAsync(() => player.PlayCount > 0, TimeSpan.FromSeconds(2));
        Assert.Equal(1, player.PlayCount);
        Assert.Equal("BuildLogger", player.LastMacroName);
        Assert.IsType<VsEventMacroTrigger>(player.LastTrigger);
    }

    [Fact]
    public void BusFires_NoMatchingMacro_PlayerNotInvoked()
    {
        var (bus, cat) = CreateBus();
        var registry = new FakeRegistry(); // empty
        var player = new FakePlayer();
        using var dispatcher = new EventTriggerDispatcher(
            registry, bus, player, CreateJtf(),
            sourceLoader: _ => "// source");

        cat.RaiseFired(new FakeEventArgs());

        // No subscription was created (registry had nothing for this name); listener never
        // runs even on the synchronous dispatch path.
        Assert.Equal(0, player.PlayCount);
    }

    [Fact]
    public async Task RegistryChanged_NewBindingPickedUp()
    {
        var (bus, cat) = CreateBus();
        var registry = new FakeRegistry();
        var player = new FakePlayer();
        using var dispatcher = new EventTriggerDispatcher(
            registry, bus, player, CreateJtf(),
            sourceLoader: _ => "// source");

        // Initial state has no bindings — fire is a no-op.
        cat.RaiseFired(new FakeEventArgs());
        Assert.Equal(0, player.PlayCount);

        // User adds a macro after the dispatcher started; registry raises Changed.
        var entry = MakeEntry("Late", CanonicalName);
        registry.EventMatches[CanonicalName] = new List<TriggerMatch>
        {
            new(entry, new TriggerBinding(TriggerKind.VsEvent, CanonicalName)),
        };
        registry.RaiseChanged();

        cat.RaiseFired(new FakeEventArgs());

        await WaitForAsync(() => player.PlayCount > 0, TimeSpan.FromSeconds(2));
        Assert.Equal(1, player.PlayCount);
    }

    [Fact]
    public async Task FailureTracker_SuccessAndFailureRecorded()
    {
        var (bus, cat) = CreateBus();
        var registry = new FakeRegistry();
        var entry = MakeEntry("Tracked", CanonicalName);
        registry.EventMatches[CanonicalName] = new List<TriggerMatch>
        {
            new(entry, new TriggerBinding(TriggerKind.VsEvent, CanonicalName)),
        };
        var tracker = new FakeFailureTracker();

        // Success first.
        var player = new FakePlayer
        {
            OnPlay = (_, _, _, _) => Task.FromResult(new MacroPlayResult(true, null, null, TimeSpan.Zero)),
        };
        using (var dispatcher = new EventTriggerDispatcher(
            registry, bus, player, CreateJtf(), tracker: tracker,
            sourceLoader: _ => "// source"))
        {
            cat.RaiseFired(new FakeEventArgs());
            await WaitForAsync(() => tracker.Successes > 0, TimeSpan.FromSeconds(2));
        }

        Assert.Equal(1, tracker.Successes);
        Assert.Equal(0, tracker.Failures);

        // Failure next.
        var (bus2, cat2) = CreateBus();
        var player2 = new FakePlayer
        {
            OnPlay = (_, _, _, _) => Task.FromResult(new MacroPlayResult(false, null, new InvalidOperationException("boom"), TimeSpan.Zero)),
        };
        using (var dispatcher = new EventTriggerDispatcher(
            registry, bus2, player2, CreateJtf(), tracker: tracker,
            sourceLoader: _ => "// source"))
        {
            cat2.RaiseFired(new FakeEventArgs());
            await WaitForAsync(() => tracker.Failures > 0, TimeSpan.FromSeconds(2));
        }

        Assert.Equal(1, tracker.Successes);
        Assert.Equal(1, tracker.Failures);
    }

    [Fact]
    public void Dispose_DroppingSubscription_StopsDispatch()
    {
        var (bus, cat) = CreateBus();
        var registry = new FakeRegistry();
        var entry = MakeEntry("Dropped", CanonicalName);
        registry.EventMatches[CanonicalName] = new List<TriggerMatch>
        {
            new(entry, new TriggerBinding(TriggerKind.VsEvent, CanonicalName)),
        };
        var player = new FakePlayer();
        var dispatcher = new EventTriggerDispatcher(
            registry, bus, player, CreateJtf(),
            sourceLoader: _ => "// source");

        dispatcher.Dispose();
        cat.RaiseFired(new FakeEventArgs());

        // Subscription was disposed; bus has no listeners; player is never called.
        Assert.Equal(0, player.PlayCount);
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (!predicate() && DateTime.UtcNow - start < timeout)
        {
            await Task.Delay(20);
        }
    }

    // --- fakes ----------------------------------------------------------------

    private sealed class FakePlayer : IMacroPlayer
    {
        public Func<string, string, IMacroTrigger?, CancellationToken, Task<MacroPlayResult>> OnPlay { get; set; }
            = (_, _, _, _) => Task.FromResult(new MacroPlayResult(true, null, null, TimeSpan.Zero));

        public int PlayCount { get; private set; }
        public string? LastMacroName { get; private set; }
        public IMacroTrigger? LastTrigger { get; private set; }

        public Task<MacroPlayResult> PlayAsync(string source, string macroName, IMacroTrigger? trigger, CancellationToken cancellation, string? csxFilePath = null)
        {
            PlayCount++;
            LastMacroName = macroName;
            LastTrigger = trigger;
            return OnPlay(source, macroName, trigger, cancellation);
        }
    }

    private sealed class FakeRegistry : IMacroTriggerRegistry
    {
        public Dictionary<string, List<TriggerMatch>> EventMatches { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool IsLoaded => true;
        public event EventHandler? Changed;
        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

        public IReadOnlyList<TriggerMatch> FindByEvent(string canonicalEventName)
            => EventMatches.TryGetValue(canonicalEventName, out var l) ? l : Array.Empty<TriggerMatch>();

        public IReadOnlyList<TriggerMatch> FindByBeforeCommand(string commandName) => Array.Empty<TriggerMatch>();
        public IReadOnlyList<TriggerMatch> FindByAfterCommand(string commandName) => Array.Empty<TriggerMatch>();
        public bool HasBeforeCommand(string commandName) => false;
        public bool HasAfterCommand(string commandName) => false;
        public Task RefreshAsync(CancellationToken cancellation = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class FakeFailureTracker : IMacroFailureTracker
    {
        public int Successes { get; private set; }
        public int Failures { get; private set; }

        public event EventHandler<MacroAutoDisabledEventArgs>? AutoDisabled { add { } remove { } }

        public IReadOnlyList<string> GetDisabledPaths() => Array.Empty<string>();
        public bool IsAutoDisabled(string macroPath) => false;
        public void ReEnable(string macroPath) { }

        public bool RecordFailure(string macroPath) { Failures++; return false; }
        public void RecordSuccess(string macroPath) { Successes++; }
    }
}
