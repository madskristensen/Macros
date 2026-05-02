using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine;
using Macros.Engine.Player;
using Macros.Engine.Triggers;
using Microsoft.VisualStudio.Threading;
using Xunit;

namespace Macros.Tests;

/// <summary>
/// Pins the <see cref="MacroService.CancelActivePlay"/> contract introduced in
/// <c>m2-cancellation</c>: the method cancels an in-flight play, and the result carries
/// an <see cref="OperationCanceledException"/> as <c>RuntimeError</c>.
/// </summary>
public sealed class MacroServiceCancelPlayTests
{
    private static MacroService CreateService(
        Func<CancellationToken, Task<IMacroPlayer>>? playerFactory = null)
    {
#pragma warning disable VSSDK005
        var ctx = new JoinableTaskContext();
#pragma warning restore VSSDK005
        return new MacroService(ctx.Factory, maxStepsProvider: null, playerFactory);
    }

    // -----------------------------------------------------------------------
    // CancelActivePlay while Playing
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CancelActivePlay_WhenPlaying_CancelsAndResultHasOCE()
    {
        // A player that signals when it starts, then blocks until its token is cancelled.
        var started = new TaskCompletionSource<bool>();
        var player = new CancelAwarePlayer(started);
        var svc = CreateService(_ => Task.FromResult<IMacroPlayer>(player));

        await svc.StartRecordingAsync();
        await svc.StopRecordingAsync();

        Task<MacroPlayResult> playTask = svc.PlayCurrentAsync();

        // Wait until the player has signalled it's running.
        await started.Task;

        svc.CancelActivePlay();

        MacroPlayResult result = await playTask;

        Assert.False(result.Success);
        Assert.IsAssignableFrom<OperationCanceledException>(result.RuntimeError);
        Assert.Equal(MacroState.Idle, svc.State);
    }

    [Fact]
    public async Task CancelActivePlay_WhenPlaying_StateReturnsToIdle()
    {
        var started = new TaskCompletionSource<bool>();
        var player = new CancelAwarePlayer(started);
        var svc = CreateService(_ => Task.FromResult<IMacroPlayer>(player));

        await svc.StartRecordingAsync();
        await svc.StopRecordingAsync();

        Task<MacroPlayResult> playTask = svc.PlayCurrentAsync();
        await started.Task;

        svc.CancelActivePlay();
        await playTask;

        Assert.Equal(MacroState.Idle, svc.State);
    }

    // -----------------------------------------------------------------------
    // CancelActivePlay when not Playing — must be a no-op
    // -----------------------------------------------------------------------

    [Fact]
    public void CancelActivePlay_WhenIdle_IsNoOp()
    {
        var svc = CreateService();

        // Must not throw.
        svc.CancelActivePlay();

        Assert.Equal(MacroState.Idle, svc.State);
    }

    [Fact]
    public async Task CancelActivePlay_WhenRecording_IsNoOp()
    {
        var svc = CreateService();
        await svc.StartRecordingAsync();

        // Must not throw.
        svc.CancelActivePlay();

        // Recording still in progress — nothing should have changed.
        Assert.Equal(MacroState.Recording, svc.State);
    }

    // -----------------------------------------------------------------------
    // _activeCts lifecycle — null after play completes (success or cancel)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ActiveCts_IsNullAfterSuccessfulPlay()
    {
        var fake = new InstantPlayer(new MacroPlayResult(true, null, null, TimeSpan.Zero));
        var svc = CreateService(_ => Task.FromResult<IMacroPlayer>(fake));

        await svc.StartRecordingAsync();
        await svc.StopRecordingAsync();
        await svc.PlayCurrentAsync();

        Assert.Null(svc.ActiveCtsForTest);
    }

    [Fact]
    public async Task ActiveCts_IsNullAfterCancelledPlay()
    {
        var started = new TaskCompletionSource<bool>();
        var player = new CancelAwarePlayer(started);
        var svc = CreateService(_ => Task.FromResult<IMacroPlayer>(player));

        await svc.StartRecordingAsync();
        await svc.StopRecordingAsync();

        Task<MacroPlayResult> playTask = svc.PlayCurrentAsync();
        await started.Task;
        svc.CancelActivePlay();
        await playTask;

        Assert.Null(svc.ActiveCtsForTest);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Signals <paramref name="started"/> once PlayAsync is entered, then blocks until the
    /// cancellation token fires and returns a result with <see cref="OperationCanceledException"/>.
    /// </summary>
    private sealed class CancelAwarePlayer : IMacroPlayer
    {
        private readonly TaskCompletionSource<bool> _started;

        public CancelAwarePlayer(TaskCompletionSource<bool> started) => _started = started;

        public async Task<MacroPlayResult> PlayAsync(
            string source,
            string macroName,
            IMacroTrigger? trigger,
            CancellationToken cancellation,
            string? csxFilePath = null)
        {
            _started.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.Infinite, cancellation).ConfigureAwait(false);
                return new MacroPlayResult(true, null, null, TimeSpan.Zero); // never reached
            }
            catch (OperationCanceledException ex)
            {
                return new MacroPlayResult(false, null, ex, TimeSpan.Zero);
            }
        }
    }

    /// <summary>Returns a fixed <see cref="MacroPlayResult"/> synchronously.</summary>
    private sealed class InstantPlayer : IMacroPlayer
    {
        private readonly MacroPlayResult _result;

        public InstantPlayer(MacroPlayResult result) => _result = result;

        public Task<MacroPlayResult> PlayAsync(
            string source,
            string macroName,
            IMacroTrigger? trigger,
            CancellationToken cancellation,
            string? csxFilePath = null)
            => Task.FromResult(_result);
    }
}
