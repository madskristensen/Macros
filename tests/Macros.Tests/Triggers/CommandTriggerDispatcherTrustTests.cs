using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Macros.Commands;
using Macros.Engine.Player;
using Macros.Engine.Storage;
using Macros.Engine.Triggers;
using Macros.Triggers;
using Microsoft.VisualStudio.Threading;
using Xunit;

namespace Macros.Tests.Triggers;

/// <summary>
/// Pins the trust-gate filtering of <see cref="CommandTriggerDispatcher"/>: untrusted
/// repo macros must not auto-fire from a BeforeCommand / AfterCommand observer, while
/// global macros and trusted-repo macros continue to dispatch normally.
/// </summary>
public sealed class CommandTriggerDispatcherTrustTests
{
    private static readonly Guid SampleGroup = new("11111111-2222-3333-4444-555555555555");
    private const uint SampleId = 42;
    private const string SampleCommand = "File.Save";

    private static JoinableTaskFactory CreateJtf()
    {
#pragma warning disable VSSDK005
        return new JoinableTaskContext().Factory;
#pragma warning restore VSSDK005
    }

    private static CommandNameCache CreateNameCache()
    {
        var cache = (CommandNameCache)Activator.CreateInstance(typeof(CommandNameCache), nonPublic: true)!;
        cache.TryAddName(SampleGroup, SampleId, SampleCommand);
        return cache;
    }

    private static MacroEntry MakeEntry(string name, MacroScope scope, TriggerKind kind = TriggerKind.BeforeCommand)
        => new(
            name,
            scope,
            $@"X:\fake\{name}.csx",
            StepCount: 0,
            Modified: DateTimeOffset.UtcNow,
            SizeBytes: 0,
            Triggers: new[] { new TriggerBinding(kind, SampleCommand) });

    [Fact]
    public void DispatchBefore_UntrustedSolution_RepoMacro_PlayerNotInvoked()
    {
        var entry = MakeEntry("RepoMacro", MacroScope.Repo);
        var registry = new FakeRegistry();
        registry.BeforeMatches[SampleCommand] = new List<TriggerMatch>
        {
            new(entry, new TriggerBinding(TriggerKind.BeforeCommand, SampleCommand)),
        };
        var player = new FakePlayer();
        var dispatcher = new CommandTriggerDispatcher(
            registry, player, CreateNameCache(),
            beforeTimeoutMsProvider: () => 1000,
            jtf: CreateJtf(),
            sourceLoader: _ => "// source",
            trustGate: (_, _) => Task.FromResult(false)); // hard-deny — solution is untrusted

        var cancelled = dispatcher.DispatchBefore(SampleGroup, SampleId);

        Assert.False(cancelled);
        Assert.Equal(0, player.PlayCount);
    }

    [Fact]
    public void DispatchBefore_TrustedSolution_RepoMacro_PlayerInvoked()
    {
        var entry = MakeEntry("RepoMacro", MacroScope.Repo);
        var registry = new FakeRegistry();
        registry.BeforeMatches[SampleCommand] = new List<TriggerMatch>
        {
            new(entry, new TriggerBinding(TriggerKind.BeforeCommand, SampleCommand)),
        };
        var player = new FakePlayer();
        var dispatcher = new CommandTriggerDispatcher(
            registry, player, CreateNameCache(),
            beforeTimeoutMsProvider: () => 1000,
            jtf: CreateJtf(),
            sourceLoader: _ => "// source",
            trustGate: (_, _) => Task.FromResult(true));

        var cancelled = dispatcher.DispatchBefore(SampleGroup, SampleId);

        Assert.False(cancelled);
        Assert.Equal(1, player.PlayCount);
    }

    [Fact]
    public void DispatchBefore_MixedScopes_OnlyAllowedRun()
    {
        // Two macros wired to the same command: a Global one (always allowed) and a Repo
        // one (denied because the solution is untrusted). The dispatcher must run exactly
        // the allowed subset.
        var globalEntry = MakeEntry("GlobalMacro", MacroScope.Global);
        var repoEntry = MakeEntry("RepoMacro", MacroScope.Repo);
        var registry = new FakeRegistry();
        registry.BeforeMatches[SampleCommand] = new List<TriggerMatch>
        {
            new(globalEntry, new TriggerBinding(TriggerKind.BeforeCommand, SampleCommand)),
            new(repoEntry,   new TriggerBinding(TriggerKind.BeforeCommand, SampleCommand)),
        };
        var player = new FakePlayer();
        var dispatcher = new CommandTriggerDispatcher(
            registry, player, CreateNameCache(),
            beforeTimeoutMsProvider: () => 1000,
            jtf: CreateJtf(),
            sourceLoader: _ => "// source",
            trustGate: (e, _) => Task.FromResult(e.Scope == MacroScope.Global)); // mirror real TrustGate semantics

        dispatcher.DispatchBefore(SampleGroup, SampleId);

        Assert.Equal(1, player.PlayCount);
        Assert.Equal("GlobalMacro", player.LastMacroName);
    }

    [Fact]
    public async Task DispatchAfter_UntrustedSolution_RepoMacro_PlayerNotInvoked()
    {
        var entry = MakeEntry("RepoAfter", MacroScope.Repo, TriggerKind.AfterCommand);
        var registry = new FakeRegistry();
        registry.AfterMatches[SampleCommand] = new List<TriggerMatch>
        {
            new(entry, new TriggerBinding(TriggerKind.AfterCommand, SampleCommand)),
        };
        var player = new FakePlayer();
        var dispatcher = new CommandTriggerDispatcher(
            registry, player, CreateNameCache(),
            beforeTimeoutMsProvider: () => 1000,
            jtf: CreateJtf(),
            sourceLoader: _ => "// source",
            trustGate: (_, _) => Task.FromResult(false));

        dispatcher.DispatchAfter(SampleGroup, SampleId);

        // AfterCommand is fire-and-forget — give it a brief window to (not) invoke.
        await Task.Delay(100);
        Assert.Equal(0, player.PlayCount);
    }

    [Fact]
    public async Task DispatchAfter_TrustedSolution_RepoMacro_PlayerInvoked()
    {
        var entry = MakeEntry("RepoAfter", MacroScope.Repo, TriggerKind.AfterCommand);
        var registry = new FakeRegistry();
        registry.AfterMatches[SampleCommand] = new List<TriggerMatch>
        {
            new(entry, new TriggerBinding(TriggerKind.AfterCommand, SampleCommand)),
        };
        var player = new FakePlayer();
        var dispatcher = new CommandTriggerDispatcher(
            registry, player, CreateNameCache(),
            beforeTimeoutMsProvider: () => 1000,
            jtf: CreateJtf(),
            sourceLoader: _ => "// source",
            trustGate: (_, _) => Task.FromResult(true));

        dispatcher.DispatchAfter(SampleGroup, SampleId);

        await WaitForAsync(() => player.PlayCount > 0, TimeSpan.FromSeconds(2));
        Assert.Equal(1, player.PlayCount);
    }

    [Fact]
    public void DispatchBefore_TrustGateThrows_FailsClosed_PlayerNotInvoked()
    {
        var entry = MakeEntry("RepoMacro", MacroScope.Repo);
        var registry = new FakeRegistry();
        registry.BeforeMatches[SampleCommand] = new List<TriggerMatch>
        {
            new(entry, new TriggerBinding(TriggerKind.BeforeCommand, SampleCommand)),
        };
        var player = new FakePlayer();
        var dispatcher = new CommandTriggerDispatcher(
            registry, player, CreateNameCache(),
            beforeTimeoutMsProvider: () => 1000,
            jtf: CreateJtf(),
            sourceLoader: _ => "// source",
            trustGate: (_, _) => throw new InvalidOperationException("gate exploded"));

        var cancelled = dispatcher.DispatchBefore(SampleGroup, SampleId);

        Assert.False(cancelled);
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

        public Task<MacroPlayResult> PlayAsync(string source, string macroName, IMacroTrigger? trigger, CancellationToken cancellation, string? csxFilePath = null)
        {
            PlayCount++;
            LastMacroName = macroName;
            return OnPlay(source, macroName, trigger, cancellation);
        }
    }

    private sealed class FakeRegistry : IMacroTriggerRegistry
    {
        public Dictionary<string, List<TriggerMatch>> BeforeMatches { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<TriggerMatch>> AfterMatches { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool IsLoaded => true;
        public event EventHandler? Changed { add { } remove { } }

        public IReadOnlyList<TriggerMatch> FindByEvent(string canonicalEventName) => Array.Empty<TriggerMatch>();
        public IReadOnlyList<TriggerMatch> FindByBeforeCommand(string commandName)
            => BeforeMatches.TryGetValue(commandName, out var l) ? l : Array.Empty<TriggerMatch>();
        public IReadOnlyList<TriggerMatch> FindByAfterCommand(string commandName)
            => AfterMatches.TryGetValue(commandName, out var l) ? l : Array.Empty<TriggerMatch>();
        public bool HasBeforeCommand(string commandName) => BeforeMatches.ContainsKey(commandName);
        public bool HasAfterCommand(string commandName) => AfterMatches.ContainsKey(commandName);
        public Task RefreshAsync(CancellationToken cancellation = default) => Task.CompletedTask;
        public void Dispose() { }
    }
}
