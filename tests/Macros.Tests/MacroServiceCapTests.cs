using System;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine;
using Microsoft.VisualStudio.Threading;
using Xunit;

namespace Macros.Tests;

/// <summary>
/// End-to-end tests for the recording-cap path through <see cref="MacroService"/>:
/// verifies that the service transitions to Idle and raises
/// <see cref="IMacroService.RecordingCapReached"/> when the session cap fires.
/// </summary>
public sealed class MacroServiceCapTests
{
    private static MacroService CreateService(int maxSteps)
    {
#pragma warning disable VSSDK005
        var ctx = new JoinableTaskContext();
#pragma warning restore VSSDK005
        return new MacroService(ctx.Factory, maxStepsProvider: () => maxSteps);
    }

    private static readonly Guid SampleGroup = new("AAAAAAAA-BBBB-CCCC-DDDD-FFFFFFFFFFFF");

    [Fact]
    public async Task CapFired_ServiceTransitionsToIdle_AndRaisesRecordingCapReached()
    {
        var svc = CreateService(maxSteps: 2);
        await svc.StartRecordingAsync();

        bool capReachedFired = false;
        svc.RecordingCapReached += (_, _) => capReachedFired = true;

        var session = svc.CurrentSession!;

        // 3 commands with maxSteps=2:
        //   cmd1 → pending
        //   cmd2 → commits cmd1 (count=1, < 2)
        //   cmd3 → commits cmd2 (count=2, == cap) → CapReached → service.StopRecordingAsync
        for (uint i = 1; i <= 3; i++)
        {
            session.OnCommand(SampleGroup, i, $"Cmd.{i}");
        }

        // StopRecordingAsync is synchronous (returns Task.FromResult); the RunAsync
        // delegate completes inline, so State and RecordingCapReached are settled by now.
        Assert.Equal(MacroState.Idle, svc.State);
        Assert.True(capReachedFired);
    }

    [Fact]
    public async Task CapFired_CurrentSessionIsNull_AfterCap()
    {
        var svc = CreateService(maxSteps: 2);
        await svc.StartRecordingAsync();

        var session = svc.CurrentSession!;

        for (uint i = 1; i <= 3; i++)
        {
            session.OnCommand(SampleGroup, i, $"Cmd.{i}");
        }

        Assert.Null(svc.CurrentSession);
    }

    [Fact]
    public async Task NoMaxStepsProvider_NeverFiresCapReached()
    {
        // Service constructed without a maxStepsProvider — cap should never trigger.
#pragma warning disable VSSDK005
        var ctx = new JoinableTaskContext();
#pragma warning restore VSSDK005
        var svc = new MacroService(ctx.Factory); // no provider → int.MaxValue

        await svc.StartRecordingAsync();

        bool capFired = false;
        svc.RecordingCapReached += (_, _) => capFired = true;

        var session = svc.CurrentSession!;

        for (uint i = 1; i <= 100; i++)
        {
            session.OnCommand(SampleGroup, i, $"Cmd.{i}");
        }

        Assert.False(capFired);
        Assert.Equal(MacroState.Recording, svc.State);
    }

    [Fact]
    public async Task CurrentRecordingMaxSteps_ReturnsSessionValue_WhileRecording()
    {
        var svc = CreateService(maxSteps: 77);
        await svc.StartRecordingAsync();

        Assert.Equal(77, svc.CurrentRecordingMaxSteps);
    }

    [Fact]
    public void CurrentRecordingMaxSteps_ReturnsIntMaxValue_WhenIdle()
    {
        var svc = CreateService(maxSteps: 77);

        Assert.Equal(int.MaxValue, svc.CurrentRecordingMaxSteps);
    }
}
