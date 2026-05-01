using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine;
using Macros.Engine.Player;
using Microsoft.VisualStudio.Threading;
using Xunit;

namespace Macros.Tests;

/// <summary>
/// Pin the new <see cref="IMacroService.PlayCurrentAsync"/> contract introduced in
/// <c>m2-error-surfacing</c>: the method now returns <see cref="MacroPlayResult"/>, populates
/// <see cref="IMacroService.CurrentMacroName"/> after a Stop, and synthesises a failure
/// result instead of throwing when there is no recorded source.
/// </summary>
/// <remarks>
/// The path that actually compiles and runs Roslyn script (resolving <see cref="IMacroPlayer"/>
/// from the VS service container) cannot be exercised here — it requires a hosted shell. Those
/// scenarios live in <c>Macros.IntegrationTests</c>; this file pins everything reachable from a
/// pure unit-test process plus the optional <c>playerFactory</c> ctor parameter that lets us
/// inject a fake player without spinning up VS.
/// </remarks>
public sealed class MacroServicePlayResultTests
{
    private static MacroService CreateService(
        Func<int>? maxStepsProvider = null,
        Func<CancellationToken, Task<IMacroPlayer>>? playerFactory = null)
    {
#pragma warning disable VSSDK005
        var ctx = new JoinableTaskContext();
#pragma warning restore VSSDK005
        return new MacroService(ctx.Factory, maxStepsProvider, playerFactory);
    }

    [Fact]
    public async Task StopRecordingAsync_PopulatesCurrentMacroNameAndSource()
    {
        var svc = CreateService();
        await svc.StartRecordingAsync();

        string returned = await svc.StopRecordingAsync();

        Assert.False(string.IsNullOrEmpty(returned));
        Assert.Equal(returned, svc.CurrentMacroSource);
        Assert.False(string.IsNullOrEmpty(svc.CurrentMacroName));
    }

    [Fact]
    public async Task PlayCurrentAsync_NoRecordedMacro_ReturnsFailureResult()
    {
        var svc = CreateService();

        MacroPlayResult result = await svc.PlayCurrentAsync();

        Assert.False(result.Success);
        Assert.Null(result.RuntimeError);
        Assert.False(string.IsNullOrEmpty(result.CompilationError));
        Assert.Contains("No macro to play", result.CompilationError, StringComparison.Ordinal);
        Assert.Equal(MacroState.Idle, svc.State);
    }

    [Fact]
    public async Task PlayCurrentAsync_FromNonIdleState_Throws()
    {
        var svc = CreateService();
        await svc.StartRecordingAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.PlayCurrentAsync());
        // State must remain Recording — the throw is on entry, before any transition.
        Assert.Equal(MacroState.Recording, svc.State);
    }

    [Fact]
    public async Task PlayCurrentAsync_AfterStop_DelegatesToInjectedPlayer()
    {
        var fake = new FakePlayer(new MacroPlayResult(true, null, null, TimeSpan.FromMilliseconds(7)));
        var svc = CreateService(playerFactory: _ => Task.FromResult<IMacroPlayer>(fake));

        await svc.StartRecordingAsync();
        await svc.StopRecordingAsync();

        MacroPlayResult result = await svc.PlayCurrentAsync();

        Assert.True(result.Success);
        Assert.Equal(TimeSpan.FromMilliseconds(7), result.Duration);
        Assert.Same(svc.CurrentMacroSource, fake.LastSource);
        Assert.Equal(svc.CurrentMacroName, fake.LastName);
        Assert.Equal("Manual", fake.LastTriggerKind);
    }

    [Fact]
    public async Task PlayCurrentAsync_ForwardsPlayerFailureToCaller()
    {
        var failure = new MacroPlayResult(false, "(1,1): error CS1002: ; expected", null, TimeSpan.FromMilliseconds(3));
        var fake = new FakePlayer(failure);
        var svc = CreateService(playerFactory: _ => Task.FromResult<IMacroPlayer>(fake));
        await svc.StartRecordingAsync();
        await svc.StopRecordingAsync();

        MacroPlayResult result = await svc.PlayCurrentAsync();

        Assert.False(result.Success);
        Assert.Equal(failure.CompilationError, result.CompilationError);
        Assert.Equal(MacroState.Idle, svc.State);
    }

    [Fact]
    public async Task PlayCurrentAsync_AfterStop_CyclesIdleToPlayingToIdle()
    {
        var fake = new FakePlayer(new MacroPlayResult(true, null, null, TimeSpan.Zero));
        var svc = CreateService(playerFactory: _ => Task.FromResult<IMacroPlayer>(fake));
        await svc.StartRecordingAsync();
        await svc.StopRecordingAsync();

        var transitions = new List<MacroStateChangedEventArgs>();
        svc.StateChanged += (_, e) => transitions.Add(e);

        await svc.PlayCurrentAsync();

        Assert.Equal(2, transitions.Count);
        Assert.Equal(MacroState.Idle, transitions[0].OldState);
        Assert.Equal(MacroState.Playing, transitions[0].NewState);
        Assert.Equal(MacroState.Playing, transitions[1].OldState);
        Assert.Equal(MacroState.Idle, transitions[1].NewState);
    }

    private sealed class FakePlayer : IMacroPlayer
    {
        private readonly MacroPlayResult _result;
        public string? LastSource { get; private set; }
        public string? LastName { get; private set; }
        public string? LastTriggerKind { get; private set; }

        public FakePlayer(MacroPlayResult result) => _result = result;

        public Task<MacroPlayResult> PlayAsync(
            string source,
            string macroName,
            string triggerKind,
            IReadOnlyDictionary<string, object?>? trigger,
            CancellationToken cancellation)
        {
            LastSource = source;
            LastName = macroName;
            LastTriggerKind = triggerKind;
            return Task.FromResult(_result);
        }
    }
}
