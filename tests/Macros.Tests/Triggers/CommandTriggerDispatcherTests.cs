using System;
using System.Collections.Generic;
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
/// Pins the contract of <see cref="CommandTriggerDispatcher"/>: synchronous BeforeCommand
/// dispatch with cancel-aggregation, asynchronous fire-and-forget AfterCommand dispatch,
/// failure tracker integration, and the timeout fail-safe.
/// </summary>
public sealed class CommandTriggerDispatcherTests
{
    private static readonly Guid SampleGroup = new("11111111-2222-3333-4444-555555555555");
    private const uint SampleId = 42;
    private const string SampleCommand = "File.Save";

    private static JoinableTaskFactory CreateJtf()
    {
#pragma warning disable VSSDK005 // ThreadHelper.JoinableTaskContext is not available in unit tests.
        return new JoinableTaskContext().Factory;
#pragma warning restore VSSDK005
    }

    private static CommandNameCache CreateNameCache(Guid group, uint id, string name)
    {
        // The cache is a process-singleton in production but the constructor is internal
        // and accessible to tests; instantiating a fresh one keeps test runs hermetic.
        var cache = (CommandNameCache)Activator.CreateInstance(typeof(CommandNameCache), nonPublic: true)!;
        cache.TryAddName(group, id, name);
        return cache;
    }

    private static MacroEntry MakeEntry(string name, string commandName, TriggerKind kind = TriggerKind.BeforeCommand)
        => new(
            name,
            MacroScope.Global,
            $"X:\\fake\\{name}.csx",
            StepCount: 0,
            Modified: DateTimeOffset.UtcNow,
            SizeBytes: 0,
            Triggers: new[] { new TriggerBinding(kind, commandName) });

    [Fact]
    public void DispatchBefore_NoMatchingTriggers_ReturnsFalse_PlayerNotInvoked()
    {
        var registry = new FakeRegistry();
        var player = new FakePlayer();
        var cache = CreateNameCache(SampleGroup, SampleId, SampleCommand);
        var dispatcher = new CommandTriggerDispatcher(
            registry, player, cache,
            beforeTimeoutMsProvider: () => 1000,
            jtf: CreateJtf(),
            sourceLoader: _ => "// source");

        var result = dispatcher.DispatchBefore(SampleGroup, SampleId);

        Assert.False(result);
        Assert.Equal(0, player.PlayCount);
    }

    [Fact]
    public void DispatchBefore_UnknownCommandId_ReturnsFalse()
    {
        // Cache is empty — the (group,id) → name lookup fails before we even hit the
        // registry, so the dispatcher must short-circuit cheaply.
        var registry = new FakeRegistry();
        registry.BeforeMatches[SampleCommand] = new List<TriggerMatch>
        {
            new(MakeEntry("Logger", SampleCommand), new TriggerBinding(TriggerKind.BeforeCommand, SampleCommand)),
        };
        var player = new FakePlayer();
        var cache = (CommandNameCache)Activator.CreateInstance(typeof(CommandNameCache), nonPublic: true)!;

        var dispatcher = new CommandTriggerDispatcher(
            registry, player, cache,
            beforeTimeoutMsProvider: () => 1000,
            jtf: CreateJtf(),
            sourceLoader: _ => "// source");

        var result = dispatcher.DispatchBefore(SampleGroup, SampleId);

        Assert.False(result);
        Assert.Equal(0, player.PlayCount);
    }

    [Fact]
    public void DispatchBefore_OneMatchNoCancel_ReturnsFalse_PlayerInvokedOnce()
    {
        var entry = MakeEntry("Logger", SampleCommand);
        var registry = new FakeRegistry();
        registry.BeforeMatches[SampleCommand] = new List<TriggerMatch>
        {
            new(entry, new TriggerBinding(TriggerKind.BeforeCommand, SampleCommand)),
        };
        var player = new FakePlayer
        {
            // Macro runs to completion without calling CancelCommand — default behaviour.
            OnPlay = (src, name, trigger, ct) => Task.FromResult(SuccessResult()),
        };
        var cache = CreateNameCache(SampleGroup, SampleId, SampleCommand);
        var dispatcher = new CommandTriggerDispatcher(
            registry, player, cache,
            beforeTimeoutMsProvider: () => 1000,
            jtf: CreateJtf(),
            sourceLoader: _ => "// source");

        var result = dispatcher.DispatchBefore(SampleGroup, SampleId);

        Assert.False(result);
        Assert.Equal(1, player.PlayCount);
        Assert.Equal(entry.Name, player.LastMacroName);
    }

    [Fact]
    public void DispatchBefore_OneMatchCancels_ReturnsTrue()
    {
        var entry = MakeEntry("Validator", SampleCommand);
        var registry = new FakeRegistry();
        registry.BeforeMatches[SampleCommand] = new List<TriggerMatch>
        {
            new(entry, new TriggerBinding(TriggerKind.BeforeCommand, SampleCommand)),
        };
        var player = new FakePlayer
        {
            OnPlay = (src, name, trigger, ct) =>
            {
                trigger?.CancelCommand();
                return Task.FromResult(SuccessResult());
            },
        };
        var cache = CreateNameCache(SampleGroup, SampleId, SampleCommand);
        var dispatcher = new CommandTriggerDispatcher(
            registry, player, cache,
            beforeTimeoutMsProvider: () => 1000,
            jtf: CreateJtf(),
            sourceLoader: _ => "// source");

        var result = dispatcher.DispatchBefore(SampleGroup, SampleId);

        Assert.True(result);
    }

    [Fact]
    public void DispatchBefore_TwoMatches_FirstCancels_BothStillRun_ReturnsTrue()
    {
        // Spec: a cancelling validator paired with a logging macro must BOTH run. Cancel
        // is the aggregated outcome, not a short-circuit.
        var validator = MakeEntry("Validator", SampleCommand);
        var logger = MakeEntry("Logger", SampleCommand);
        var registry = new FakeRegistry();
        registry.BeforeMatches[SampleCommand] = new List<TriggerMatch>
        {
            new(validator, new TriggerBinding(TriggerKind.BeforeCommand, SampleCommand)),
            new(logger, new TriggerBinding(TriggerKind.BeforeCommand, SampleCommand)),
        };
        var invoked = new List<string>();
        var player = new FakePlayer
        {
            OnPlay = (src, name, trigger, ct) =>
            {
                invoked.Add(name);
                if (name == validator.Name) trigger?.CancelCommand();
                return Task.FromResult(SuccessResult());
            },
        };
        var cache = CreateNameCache(SampleGroup, SampleId, SampleCommand);
        var dispatcher = new CommandTriggerDispatcher(
            registry, player, cache,
            beforeTimeoutMsProvider: () => 1000,
            jtf: CreateJtf(),
            sourceLoader: _ => "// source");

        var result = dispatcher.DispatchBefore(SampleGroup, SampleId);

        Assert.True(result);
        Assert.Equal(new[] { validator.Name, logger.Name }, invoked);
    }

    [Fact]
    public async Task DispatchAfter_NoMatches_NoOp()
    {
        var registry = new FakeRegistry(); // empty
        var player = new FakePlayer();
        var cache = CreateNameCache(SampleGroup, SampleId, SampleCommand);
        var dispatcher = new CommandTriggerDispatcher(
            registry, player, cache,
            beforeTimeoutMsProvider: () => 1000,
            jtf: CreateJtf(),
            sourceLoader: _ => "// source");

        dispatcher.DispatchAfter(SampleGroup, SampleId);

        // Give the (would-be) fire-and-forget a chance to run; assert nothing happened.
        await Task.Delay(50);
        Assert.Equal(0, player.PlayCount);
    }

    [Fact]
    public async Task DispatchAfter_OneMatch_PlayerInvokedAsync()
    {
        var entry = MakeEntry("AfterSaveLogger", SampleCommand, TriggerKind.AfterCommand);
        var registry = new FakeRegistry();
        registry.AfterMatches[SampleCommand] = new List<TriggerMatch>
        {
            new(entry, new TriggerBinding(TriggerKind.AfterCommand, SampleCommand)),
        };
        var played = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new FakePlayer
        {
            OnPlay = (src, name, trigger, ct) =>
            {
                played.TrySetResult(name);
                return Task.FromResult(SuccessResult());
            },
        };
        var cache = CreateNameCache(SampleGroup, SampleId, SampleCommand);
        var dispatcher = new CommandTriggerDispatcher(
            registry, player, cache,
            beforeTimeoutMsProvider: () => 1000,
            jtf: CreateJtf(),
            sourceLoader: _ => "// source");

        dispatcher.DispatchAfter(SampleGroup, SampleId);

        var completed = await Task.WhenAny(played.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(played.Task, completed);
        Assert.Equal(entry.Name, await played.Task);
    }

    [Fact]
    public void DispatchBefore_FailedPlay_RecordsFailureOnTracker()
    {
        var entry = MakeEntry("Buggy", SampleCommand);
        var registry = new FakeRegistry();
        registry.BeforeMatches[SampleCommand] = new List<TriggerMatch>
        {
            new(entry, new TriggerBinding(TriggerKind.BeforeCommand, SampleCommand)),
        };
        var player = new FakePlayer
        {
            OnPlay = (src, name, trigger, ct) => Task.FromResult(FailureResult(new InvalidOperationException("boom"))),
        };
        var tracker = new FakeFailureTracker();
        var cache = CreateNameCache(SampleGroup, SampleId, SampleCommand);
        var dispatcher = new CommandTriggerDispatcher(
            registry, player, cache,
            beforeTimeoutMsProvider: () => 1000,
            jtf: CreateJtf(),
            tracker: tracker,
            sourceLoader: _ => "// source");

        var result = dispatcher.DispatchBefore(SampleGroup, SampleId);

        Assert.False(result);
        Assert.Equal(0, tracker.Successes);
        Assert.Equal(1, tracker.Failures);
        Assert.Equal(entry.Path, tracker.LastFailurePath);
    }

    [Fact]
    public void DispatchBefore_SuccessfulPlay_RecordsSuccessOnTracker()
    {
        var entry = MakeEntry("Healthy", SampleCommand);
        var registry = new FakeRegistry();
        registry.BeforeMatches[SampleCommand] = new List<TriggerMatch>
        {
            new(entry, new TriggerBinding(TriggerKind.BeforeCommand, SampleCommand)),
        };
        var player = new FakePlayer
        {
            OnPlay = (src, name, trigger, ct) => Task.FromResult(SuccessResult()),
        };
        var tracker = new FakeFailureTracker();
        var cache = CreateNameCache(SampleGroup, SampleId, SampleCommand);
        var dispatcher = new CommandTriggerDispatcher(
            registry, player, cache,
            beforeTimeoutMsProvider: () => 1000,
            jtf: CreateJtf(),
            tracker: tracker,
            sourceLoader: _ => "// source");

        dispatcher.DispatchBefore(SampleGroup, SampleId);

        Assert.Equal(1, tracker.Successes);
        Assert.Equal(0, tracker.Failures);
        Assert.Equal(entry.Path, tracker.LastSuccessPath);
    }

    [Fact]
    public void DispatchBefore_TimeoutExceeded_RecordsFailure_NoCancelSignal()
    {
        // Player respects the cancellation token by awaiting Task.Delay(longer than timeout).
        // The dispatcher's CancellationTokenSource(timeoutMs) should fire, the player should
        // throw OperationCanceledException, the dispatcher should swallow it, record failure,
        // and NOT raise the cancel signal.
        var entry = MakeEntry("SlowMacro", SampleCommand);
        var registry = new FakeRegistry();
        registry.BeforeMatches[SampleCommand] = new List<TriggerMatch>
        {
            new(entry, new TriggerBinding(TriggerKind.BeforeCommand, SampleCommand)),
        };
        var player = new FakePlayer
        {
            OnPlay = async (src, name, trigger, ct) =>
            {
                // Honour the token — when it fires we surface OperationCanceledException.
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return SuccessResult();
            },
        };
        var tracker = new FakeFailureTracker();
        var cache = CreateNameCache(SampleGroup, SampleId, SampleCommand);
        var dispatcher = new CommandTriggerDispatcher(
            registry, player, cache,
            beforeTimeoutMsProvider: () => 25, // 25 ms timeout — fires immediately.
            jtf: CreateJtf(),
            tracker: tracker,
            sourceLoader: _ => "// source");

        var result = dispatcher.DispatchBefore(SampleGroup, SampleId);

        Assert.False(result, "timeout must NOT raise the cancel signal — fail-safe is to let the command run");
        Assert.Equal(1, tracker.Failures);
        Assert.Equal(0, tracker.Successes);
    }

    [Fact]
    public void DispatchBefore_PlayerThrowsException_RecordsFailure_NoCancelSignal()
    {
        // Gap: exercises the catch(Exception) path in DispatchBefore, distinct from the
        // "player returns a failure result" path tested by DispatchBefore_FailedPlay_*.
        var entry = MakeEntry("Exploding", SampleCommand);
        var registry = new FakeRegistry();
        registry.BeforeMatches[SampleCommand] = new List<TriggerMatch>
        {
            new(entry, new TriggerBinding(TriggerKind.BeforeCommand, SampleCommand)),
        };
        var player = new FakePlayer
        {
            // Return a faulted Task — the dispatcher's async runner will surface this
            // as an exception that lands in catch(Exception) { _tracker.RecordFailure }.
            OnPlay = (_, _, _, _) =>
                Task.FromException<MacroPlayResult>(new InvalidOperationException("player exploded")),
        };
        var tracker = new FakeFailureTracker();
        var cache = CreateNameCache(SampleGroup, SampleId, SampleCommand);
        var dispatcher = new CommandTriggerDispatcher(
            registry, player, cache,
            beforeTimeoutMsProvider: () => 1000,
            jtf: CreateJtf(),
            tracker: tracker,
            sourceLoader: _ => "// source");

        var result = dispatcher.DispatchBefore(SampleGroup, SampleId);

        Assert.False(result, "an exception must NOT raise the cancel signal — fail-safe is to let the command run");
        Assert.Equal(1, tracker.Failures);
        Assert.Equal(0, tracker.Successes);
        Assert.Equal(entry.Path, tracker.LastFailurePath);
    }

    [Fact]
    public void DispatchBefore_SourceLoaderReturnsNull_RecordsFailure_NoCancelSignal()
    {
        var entry = MakeEntry("MissingFile", SampleCommand);
        var registry = new FakeRegistry();
        registry.BeforeMatches[SampleCommand] = new List<TriggerMatch>
        {
            new(entry, new TriggerBinding(TriggerKind.BeforeCommand, SampleCommand)),
        };
        var player = new FakePlayer();
        var tracker = new FakeFailureTracker();
        var cache = CreateNameCache(SampleGroup, SampleId, SampleCommand);
        var dispatcher = new CommandTriggerDispatcher(
            registry, player, cache,
            beforeTimeoutMsProvider: () => 1000,
            jtf: CreateJtf(),
            tracker: tracker,
            sourceLoader: _ => null);

        var result = dispatcher.DispatchBefore(SampleGroup, SampleId);

        Assert.False(result);
        Assert.Equal(0, player.PlayCount);
        Assert.Equal(1, tracker.Failures);
    }

    private static MacroPlayResult SuccessResult() =>
        new(Success: true, CompilationError: null, RuntimeError: null, Duration: TimeSpan.Zero);

    private static MacroPlayResult FailureResult(Exception ex) =>
        new(Success: false, CompilationError: null, RuntimeError: ex, Duration: TimeSpan.Zero);

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
        public Dictionary<string, List<TriggerMatch>> EventMatches { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool IsLoaded => true;
        public event EventHandler? Changed { add { } remove { } }

        public IReadOnlyList<TriggerMatch> FindByEvent(string canonicalEventName)
            => EventMatches.TryGetValue(canonicalEventName, out var l) ? l : Array.Empty<TriggerMatch>();

        public IReadOnlyList<TriggerMatch> FindByBeforeCommand(string commandName)
            => BeforeMatches.TryGetValue(commandName, out var l) ? l : Array.Empty<TriggerMatch>();

        public IReadOnlyList<TriggerMatch> FindByAfterCommand(string commandName)
            => AfterMatches.TryGetValue(commandName, out var l) ? l : Array.Empty<TriggerMatch>();

        public bool HasBeforeCommand(string commandName) => BeforeMatches.ContainsKey(commandName);
        public bool HasAfterCommand(string commandName) => AfterMatches.ContainsKey(commandName);

        public Task RefreshAsync(CancellationToken cancellation = default) => Task.CompletedTask;

        public void Dispose() { }
    }

    private sealed class FakeFailureTracker : IMacroFailureTracker
    {
        public int Successes { get; private set; }
        public int Failures { get; private set; }
        public string? LastSuccessPath { get; private set; }
        public string? LastFailurePath { get; private set; }

        public event EventHandler<MacroAutoDisabledEventArgs>? AutoDisabled { add { } remove { } }

        public IReadOnlyList<string> GetDisabledPaths() => Array.Empty<string>();
        public bool IsAutoDisabled(string macroPath) => false;
        public void ReEnable(string macroPath) { }

        public bool RecordFailure(string macroPath)
        {
            Failures++;
            LastFailurePath = macroPath;
            return false;
        }

        public void RecordSuccess(string macroPath)
        {
            Successes++;
            LastSuccessPath = macroPath;
        }
    }
}
