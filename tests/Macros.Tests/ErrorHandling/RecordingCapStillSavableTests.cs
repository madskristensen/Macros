using System;
using System.Threading.Tasks;
using Macros.Engine;
using Macros.Engine.Recording;
using Microsoft.VisualStudio.Threading;
using Xunit;

namespace Macros.Tests.ErrorHandling;

/// <summary>
/// Pins the v1.0 contract that even when the recording cap fires, the user keeps the
/// partial macro: <see cref="IMacroService.CurrentMacroSource"/> is populated, the
/// engine is back in <see cref="MacroState.Idle"/>, and a follow-up Save As / Play Last
/// works. Without this guarantee the cap would feel like data loss.
/// </summary>
public sealed class RecordingCapStillSavableTests
{
    private static readonly Guid SampleGroup = new("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE");

    private static MacroService CreateService(int maxSteps)
    {
#pragma warning disable VSSDK005 // ThreadHelper.JoinableTaskContext is not available outside a hosted VS process.
        var ctx = new JoinableTaskContext();
#pragma warning restore VSSDK005
        return new MacroService(ctx.Factory, maxStepsProvider: () => maxSteps);
    }

    [Fact]
    public async Task CapReached_LeavesCurrentMacroSourcePopulated_AndReturnsToIdle()
    {
        var svc = CreateService(maxSteps: 2);
        await svc.StartRecordingAsync();
        RecordingSession session = svc.CurrentSession!;

        var capWaiter = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.CapReached += (_, _) => capWaiter.TrySetResult(null);

        // Push enough commands to commit 2 distinct steps (the aggregator coalesces same-id
        // bursts, so use 3 distinct ids — third commits the second and trips the cap).
        session.OnCommand(SampleGroup, 1, "Cmd.1");
        session.OnCommand(SampleGroup, 2, "Cmd.2");
        session.OnCommand(SampleGroup, 3, "Cmd.3");

        // Wait for CapReached → JoinableTask runs StopRecordingAsync → state goes Idle.
        await capWaiter.Task;
        // StopRecordingAsync is dispatched through JoinableTaskFactory; give it a moment
        // to complete its state transition deterministically.
        for (int i = 0; i < 50 && svc.State != MacroState.Idle; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(MacroState.Idle, svc.State);
        Assert.NotNull(svc.CurrentMacroSource);
        Assert.NotEmpty(svc.CurrentMacroSource!);
        Assert.Equal("RecordedMacro", svc.CurrentMacroName);
    }

    [Fact]
    public async Task CapReached_GeneratedSourceContainsAtLeastOneCommandStep()
    {
        var svc = CreateService(maxSteps: 1);
        await svc.StartRecordingAsync();
        RecordingSession session = svc.CurrentSession!;

        var capWaiter = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.CapReached += (_, _) => capWaiter.TrySetResult(null);

        session.OnCommand(SampleGroup, 10, "Cmd.10");
        session.OnCommand(SampleGroup, 11, "Cmd.11"); // commits 10 → trips cap
        await capWaiter.Task;
        for (int i = 0; i < 50 && svc.State != MacroState.Idle; i++)
        {
            await Task.Delay(10);
        }

        // The header is rendered by CSharpCodeGenerator; the body must reference at
        // least one execution helper so the user's "partial recording" is replayable.
        Assert.Contains("Steps:", svc.CurrentMacroSource ?? string.Empty);
    }
}
