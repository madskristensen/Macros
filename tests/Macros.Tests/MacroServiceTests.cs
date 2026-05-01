using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Macros.Engine;
using Macros.Engine.Player;
using Microsoft.VisualStudio.Threading;
using Xunit;

namespace Macros.Tests;

public sealed class MacroServiceTests
{
    private static MacroService CreateService()
    {
        // VSSDK005 normally insists on ThreadHelper.JoinableTaskContext, but that singleton
        // only exists inside a hosted VS process. These are pure engine unit tests with no
        // VS shell, so we own the JoinableTaskContext for the test's lifetime and never
        // marshal back to a real UI thread (the M1 stubs don't switch threads).
#pragma warning disable VSSDK005
        var ctx = new JoinableTaskContext();
#pragma warning restore VSSDK005
        return new MacroService(ctx.Factory);
    }

    private static List<MacroStateChangedEventArgs> CaptureStateChanges(MacroService svc)
    {
        var events = new List<MacroStateChangedEventArgs>();
        svc.StateChanged += (_, e) => events.Add(e);
        return events;
    }

    [Fact]
    public void NewService_StartsIdle()
    {
        var svc = CreateService();

        Assert.Equal(MacroState.Idle, svc.State);
        Assert.Null(svc.CurrentMacroSource);
    }

    [Fact]
    public async Task StartRecordingAsync_FromIdle_TransitionsToRecording_AndRaisesEvent()
    {
        var svc = CreateService();
        var events = CaptureStateChanges(svc);

        await svc.StartRecordingAsync();

        Assert.Equal(MacroState.Recording, svc.State);
        var evt = Assert.Single(events);
        Assert.Equal(MacroState.Idle, evt.OldState);
        Assert.Equal(MacroState.Recording, evt.NewState);
    }

    [Fact]
    public async Task StartRecordingAsync_WhileRecording_Throws()
    {
        var svc = CreateService();
        await svc.StartRecordingAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartRecordingAsync());
        Assert.Equal(MacroState.Recording, svc.State);
    }

    [Fact]
    public async Task StopRecordingAsync_AfterStart_ReturnsSourceAndStoresIt()
    {
        var svc = CreateService();
        await svc.StartRecordingAsync();

        var src = await svc.StopRecordingAsync();

        Assert.False(string.IsNullOrEmpty(src));
        Assert.Equal(MacroState.Idle, svc.State);
        Assert.Equal(src, svc.CurrentMacroSource);
    }

    [Fact]
    public async Task StopRecordingAsync_WhileIdle_Throws()
    {
        var svc = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StopRecordingAsync());
        Assert.Equal(MacroState.Idle, svc.State);
    }

    [Fact]
    public async Task PlayCurrentAsync_NoSource_ReturnsFailureWithoutStateChange()
    {
        // The signature change in m2-error-surfacing means PlayCurrentAsync no longer cycles
        // Idle → Playing → Idle when there is nothing to play; instead it returns a synthetic
        // failure result so the caller (PlayLastCommand) can route the message through the
        // Output / Error List / InfoBar surfaces. State stays put, no events fire.
        var svc = CreateService();
        var events = CaptureStateChanges(svc);

        MacroPlayResult result = await svc.PlayCurrentAsync();

        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.CompilationError));
        Assert.Null(result.RuntimeError);
        Assert.Equal(MacroState.Idle, svc.State);
        Assert.Empty(events);
    }

    [Fact]
    public async Task PlayNamedAsync_CyclesPlayingThenIdle()
    {
        var svc = CreateService();
        var events = CaptureStateChanges(svc);

        await svc.PlayNamedAsync("sample");

        Assert.Equal(MacroState.Idle, svc.State);
        Assert.Equal(2, events.Count);
        Assert.Equal(MacroState.Playing, events[0].NewState);
        Assert.Equal(MacroState.Idle, events[1].NewState);
    }

    [Fact]
    public async Task PlayNamedAsync_WithEmptyName_Throws()
    {
        var svc = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() => svc.PlayNamedAsync(""));
        Assert.Equal(MacroState.Idle, svc.State);
    }

    [Fact]
    public async Task CancelAsync_FromIdle_IsNoOp()
    {
        var svc = CreateService();
        var events = CaptureStateChanges(svc);

        await svc.CancelAsync();

        Assert.Equal(MacroState.Idle, svc.State);
        Assert.Empty(events);
    }

    [Fact]
    public async Task CancelAsync_FromRecording_ResetsToIdleAndRaisesEvent()
    {
        var svc = CreateService();
        await svc.StartRecordingAsync();
        var events = CaptureStateChanges(svc);

        await svc.CancelAsync();

        Assert.Equal(MacroState.Idle, svc.State);
        var evt = Assert.Single(events);
        Assert.Equal(MacroState.Recording, evt.OldState);
        Assert.Equal(MacroState.Idle, evt.NewState);
    }
}
