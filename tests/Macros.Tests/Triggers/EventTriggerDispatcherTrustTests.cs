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
/// Pins the trust-gate filtering of <see cref="EventTriggerDispatcher"/>: VS-event-driven
/// repo macros only fire when the active solution is trusted; global macros and trusted-repo
/// macros dispatch normally.
/// </summary>
public sealed class EventTriggerDispatcherTrustTests
{
    private const string CanonicalName = "Fake.Fired";

    public sealed class FakeEventArgs : EventArgs { }

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

    private static MacroEntry MakeEntry(string name, MacroScope scope)
        => new(
            name,
            scope,
            $@"X:\fake\{name}.csx",
            StepCount: 0,
            Modified: DateTimeOffset.UtcNow,
            SizeBytes: 0,
            Triggers: new[] { new TriggerBinding(TriggerKind.VsEvent, CanonicalName) });

    [Fact]
    public async Task BusFires_UntrustedRepoMacro_PlayerNotInvoked()
    {
        var (bus, cat) = CreateBus();
        var registry = new FakeRegistry();
        var entry = MakeEntry("RepoEvent", MacroScope.Repo);
        registry.EventMatches[CanonicalName] = new List<TriggerMatch>
        {
            new(entry, new TriggerBinding(TriggerKind.VsEvent, CanonicalName)),
        };
        var player = new FakePlayer();
        using var dispatcher = new EventTriggerDispatcher(
            registry, bus, player, CreateJtf(),
            sourceLoader: _ => "// source",
            trustGate: _ => false);

        cat.RaiseFired(new FakeEventArgs());

        // Wait briefly to confirm the player is *not* invoked rather than racing the
        // assertion against scheduling latency.
        await Task.Delay(150);
        Assert.Equal(0, player.PlayCount);
    }

    [Fact]
    public async Task BusFires_TrustedRepoMacro_PlayerInvoked()
    {
        var (bus, cat) = CreateBus();
        var registry = new FakeRegistry();
        var entry = MakeEntry("RepoEvent", MacroScope.Repo);
        registry.EventMatches[CanonicalName] = new List<TriggerMatch>
        {
            new(entry, new TriggerBinding(TriggerKind.VsEvent, CanonicalName)),
        };
        var player = new FakePlayer();
        using var dispatcher = new EventTriggerDispatcher(
            registry, bus, player, CreateJtf(),
            sourceLoader: _ => "// source",
            trustGate: _ => true);

        cat.RaiseFired(new FakeEventArgs());

        await WaitForAsync(() => player.PlayCount > 0, TimeSpan.FromSeconds(2));
        Assert.Equal(1, player.PlayCount);
        Assert.Equal("RepoEvent", player.LastMacroName);
    }

    [Fact]
    public async Task BusFires_MixedScopes_OnlyGlobalRuns()
    {
        var (bus, cat) = CreateBus();
        var registry = new FakeRegistry();
        var globalEntry = MakeEntry("GlobalEvent", MacroScope.Global);
        var repoEntry = MakeEntry("RepoEvent", MacroScope.Repo);
        registry.EventMatches[CanonicalName] = new List<TriggerMatch>
        {
            new(globalEntry, new TriggerBinding(TriggerKind.VsEvent, CanonicalName)),
            new(repoEntry,   new TriggerBinding(TriggerKind.VsEvent, CanonicalName)),
        };
        var player = new FakePlayer();
        using var dispatcher = new EventTriggerDispatcher(
            registry, bus, player, CreateJtf(),
            sourceLoader: _ => "// source",
            trustGate: e => e.Scope == MacroScope.Global);

        cat.RaiseFired(new FakeEventArgs());

        await WaitForAsync(() => player.PlayCount > 0, TimeSpan.FromSeconds(2));
        await Task.Delay(100); // give the (denied) repo entry a chance to (not) run
        Assert.Equal(1, player.PlayCount);
        Assert.Equal("GlobalEvent", player.LastMacroName);
    }

    [Fact]
    public async Task BusFires_TrustGateThrows_FailsClosed()
    {
        var (bus, cat) = CreateBus();
        var registry = new FakeRegistry();
        var entry = MakeEntry("RepoEvent", MacroScope.Repo);
        registry.EventMatches[CanonicalName] = new List<TriggerMatch>
        {
            new(entry, new TriggerBinding(TriggerKind.VsEvent, CanonicalName)),
        };
        var player = new FakePlayer();
        using var dispatcher = new EventTriggerDispatcher(
            registry, bus, player, CreateJtf(),
            sourceLoader: _ => "// source",
            trustGate: _ => throw new InvalidOperationException("gate exploded"));

        cat.RaiseFired(new FakeEventArgs());

        await Task.Delay(150);
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

        public Task<MacroPlayResult> PlayAsync(string source, string macroName, IMacroTrigger? trigger, CancellationToken cancellation)
        {
            PlayCount++;
            LastMacroName = macroName;
            return OnPlay(source, macroName, trigger, cancellation);
        }
    }

    private sealed class FakeRegistry : IMacroTriggerRegistry
    {
        public Dictionary<string, List<TriggerMatch>> EventMatches { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsLoaded => true;
        public event EventHandler? Changed { add { } remove { } }

        public IReadOnlyList<TriggerMatch> FindByEvent(string canonicalEventName)
            => EventMatches.TryGetValue(canonicalEventName, out var l) ? l : Array.Empty<TriggerMatch>();
        public IReadOnlyList<TriggerMatch> FindByBeforeCommand(string commandName) => Array.Empty<TriggerMatch>();
        public IReadOnlyList<TriggerMatch> FindByAfterCommand(string commandName) => Array.Empty<TriggerMatch>();
        public bool HasBeforeCommand(string commandName) => false;
        public bool HasAfterCommand(string commandName) => false;
        public Task RefreshAsync(CancellationToken cancellation = default) => Task.CompletedTask;
        public void Dispose() { }
    }
}
