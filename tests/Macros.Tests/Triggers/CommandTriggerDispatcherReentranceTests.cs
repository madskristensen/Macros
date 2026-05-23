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
/// Verifies that <see cref="CommandTriggerDispatcher"/> + <see cref="TriggerReentranceGuard"/>
/// suppress recursive re-dispatch of the same Before/After key.
/// </summary>
public sealed class CommandTriggerDispatcherReentranceTests
{
    private static readonly Guid SampleGroup = new("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE");
    private const uint SampleId = 99;
    private const string SampleCommand = "Edit.Paste";

#pragma warning disable VSSDK005
    private static JoinableTaskFactory CreateJtf() => new JoinableTaskContext().Factory;
#pragma warning restore VSSDK005

    private static CommandNameCache CreateNameCache()
    {
        var cache = (CommandNameCache)Activator.CreateInstance(typeof(CommandNameCache), nonPublic: true)!;
        cache.TryAddName(SampleGroup, SampleId, SampleCommand);
        return cache;
    }

    private static MacroEntry MakeEntry(string name, string cmd, TriggerKind kind)
        => new(name, MacroScope.Global, $"X:\\fake\\{name}.csx",
            StepCount: 0, Modified: DateTimeOffset.UtcNow, SizeBytes: 0,
            Triggers: new[] { new TriggerBinding(kind, cmd) });

    [Fact]
    public void DispatchBefore_ReentrantCall_IsSuppressed()
    {
        // Arrange: a player that, when invoked for BeforeCommand, calls DispatchBefore
        // again on the same dispatcher (simulating a macro that triggers its own command).
        CommandTriggerDispatcher? dispatcher = null;
        int playCount = 0;

        var registry = new ReentranceFakeRegistry();
        registry.BeforeMatches[SampleCommand] = new List<TriggerMatch>
        {
            new(MakeEntry("Macro1", SampleCommand, TriggerKind.BeforeCommand),
                new TriggerBinding(TriggerKind.BeforeCommand, SampleCommand)),
        };

        var player = new ReentranceFakePlayer(onPlay: (_, _, _, _) =>
        {
            playCount++;
            // Simulate recursive dispatch — this must be suppressed.
            dispatcher!.DispatchBefore(SampleGroup, SampleId);
            return Task.FromResult(new MacroPlayResult(true, null, null, TimeSpan.Zero));
        });

        var guard = new TriggerReentranceGuard();
        var cache = CreateNameCache();
        dispatcher = new CommandTriggerDispatcher(
            registry, player, cache,
            beforeTimeoutMsProvider: () => 2000,
            jtf: CreateJtf(),
            guard: guard,
            sourceLoader: _ => "// source");

        // Act
        dispatcher.DispatchBefore(SampleGroup, SampleId);

        // Assert: player invoked exactly once — the recursive call was suppressed.
        Assert.Equal(1, playCount);
    }

    [Fact]
    public void DispatchBefore_DeepRecursion_IsBounded_DoesNotStackOverflow()
    {
        // Regression for madskristensen/Macros#12: a macro bound to BeforeCommand X
        // whose body calls ExecuteCommandAsync("X") would recursively dispatch
        // itself when the guard didn't survive the dispatch boundary, freezing VS.
        // Even without any prior outer scope (the "manual run" case), the guard
        // must cap the recursion at MaxDepth so the call returns in bounded time.
        CommandTriggerDispatcher? dispatcher = null;
        int playCount = 0;

        var registry = new ReentranceFakeRegistry();
        registry.BeforeMatches[SampleCommand] = new List<TriggerMatch>
        {
            new(MakeEntry("SelfTriggering", SampleCommand, TriggerKind.BeforeCommand),
                new TriggerBinding(TriggerKind.BeforeCommand, SampleCommand)),
        };

        var player = new ReentranceFakePlayer(onPlay: (_, _, _, _) =>
        {
            playCount++;
            // Every macro execution attempts to re-dispatch the same command, exactly
            // like a real macro calling ExecuteCommandAsync(SampleCommand) on itself.
            dispatcher!.DispatchBefore(SampleGroup, SampleId);
            return Task.FromResult(new MacroPlayResult(true, null, null, TimeSpan.Zero));
        });

        var guard = new TriggerReentranceGuard();
        var cache = CreateNameCache();
        dispatcher = new CommandTriggerDispatcher(
            registry, player, cache,
            beforeTimeoutMsProvider: () => 2000,
            jtf: CreateJtf(),
            guard: guard,
            sourceLoader: _ => "// source");

        // Drive the dispatcher twice in a row to verify the guard releases its scope
        // after each top-level dispatch; before the fix, a leaked Releaser left depth
        // pinned at 1 forever, eventually suppressing every future BeforeCommand.
        dispatcher.DispatchBefore(SampleGroup, SampleId);
        Assert.Equal(1, playCount);

        playCount = 0;
        dispatcher.DispatchBefore(SampleGroup, SampleId);
        Assert.Equal(1, playCount);

        Assert.Equal(0, guard.CurrentDepth);
    }

    [Fact]
    public void DispatchBefore_WithGuard_NoFalsePositive_DifferentCommandNotSuppressed()
    {
        // "before:Edit.Paste" guard must not suppress "before:File.Save"
        const string otherCommand = "File.Save";
        var otherGroup = new Guid("11111111-0000-0000-0000-000000000000");
        const uint otherId = 7u;

        int pasteCount = 0, saveCount = 0;

        var registry = new ReentranceFakeRegistry();
        registry.BeforeMatches[SampleCommand] = new List<TriggerMatch>
        {
            new(MakeEntry("PasteMacro", SampleCommand, TriggerKind.BeforeCommand),
                new TriggerBinding(TriggerKind.BeforeCommand, SampleCommand)),
        };
        registry.BeforeMatches[otherCommand] = new List<TriggerMatch>
        {
            new(MakeEntry("SaveMacro", otherCommand, TriggerKind.BeforeCommand),
                new TriggerBinding(TriggerKind.BeforeCommand, otherCommand)),
        };

        CommandTriggerDispatcher? dispatcher = null;
        var player = new ReentranceFakePlayer(onPlay: (_, name, _, _) =>
        {
            if (name == "PasteMacro")
            {
                pasteCount++;
                // Trigger a DIFFERENT command — must not be suppressed.
                dispatcher!.DispatchBefore(otherGroup, otherId);
            }
            else
            {
                saveCount++;
            }
            return Task.FromResult(new MacroPlayResult(true, null, null, TimeSpan.Zero));
        });

        var guard = new TriggerReentranceGuard();
        var cache = CreateNameCache();
        cache.TryAddName(otherGroup, otherId, otherCommand);
        dispatcher = new CommandTriggerDispatcher(
            registry, player, cache,
            beforeTimeoutMsProvider: () => 2000,
            jtf: CreateJtf(),
            guard: guard,
            sourceLoader: _ => "// source");

        dispatcher.DispatchBefore(SampleGroup, SampleId);

        Assert.Equal(1, pasteCount);
        Assert.Equal(1, saveCount); // different key — should have run
    }

    // Minimal fakes reused from the existing CommandTriggerDispatcherTests pattern.

    private sealed class ReentranceFakePlayer : IMacroPlayer
    {
        private readonly Func<string, string, IMacroTrigger?, CancellationToken, Task<MacroPlayResult>> _onPlay;
        public ReentranceFakePlayer(Func<string, string, IMacroTrigger?, CancellationToken, Task<MacroPlayResult>> onPlay)
            => _onPlay = onPlay;

        public Task<MacroPlayResult> PlayAsync(string source, string macroName, IMacroTrigger? trigger, CancellationToken cancellation, string? csxFilePath = null)
            => _onPlay(source, macroName, trigger, cancellation);
    }

    private sealed class ReentranceFakeRegistry : IMacroTriggerRegistry
    {
        public Dictionary<string, List<TriggerMatch>> BeforeMatches { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<TriggerMatch>> AfterMatches { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool IsLoaded => true;
        public event EventHandler? Changed { add { } remove { } }

        public IReadOnlyList<TriggerMatch> FindByEvent(string n) => Array.Empty<TriggerMatch>();
        public IReadOnlyList<TriggerMatch> FindByBeforeCommand(string n)
            => BeforeMatches.TryGetValue(n, out var l) ? l : Array.Empty<TriggerMatch>();
        public IReadOnlyList<TriggerMatch> FindByAfterCommand(string n)
            => AfterMatches.TryGetValue(n, out var l) ? l : Array.Empty<TriggerMatch>();
        public bool HasBeforeCommand(string n) => BeforeMatches.ContainsKey(n);
        public bool HasAfterCommand(string n) => AfterMatches.ContainsKey(n);
        public Task RefreshAsync(CancellationToken cancellation = default) => Task.CompletedTask;
        public void Dispose() { }
    }
}
