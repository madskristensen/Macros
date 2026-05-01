using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Macros.Engine;
using Macros.Engine.Recording;
using Microsoft.VisualStudio.Threading;
using Xunit;

namespace Macros.Tests.Recording;

/// <summary>
/// Unit tests for the cap enforcement logic in <see cref="RecordingSession"/>:
/// <see cref="RecordingSession.CapReached"/>, <see cref="RecordingSession.StepCountChanged"/>,
/// and the behaviour of <see cref="RecordingSession.DrainAndStop"/> after cap.
/// </summary>
public sealed class RecordingCapTests
{
    private static readonly Guid SampleGroup = new("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE");

    private static MacroService CreateService(int maxSteps = int.MaxValue)
    {
#pragma warning disable VSSDK005
        var ctx = new JoinableTaskContext();
#pragma warning restore VSSDK005
        return new MacroService(ctx.Factory, maxStepsProvider: () => maxSteps);
    }

    private static void PushCommand(IRecordingSink session, uint id)
        => session.OnCommand(SampleGroup, id, $"Cmd.{id}");

    [Fact]
    public async Task CapReached_FiresOnce_WhenMaxStepsReached()
    {
        var svc = CreateService(maxSteps: 3);
        await svc.StartRecordingAsync();
        RecordingSession session = svc.CurrentSession!;

        int capCount = 0;
        session.CapReached += (_, _) => capCount++;

        // cmd1 → pending; cmd2 → commits cmd1 (count=1); cmd3 → commits cmd2 (count=2);
        // cmd4 → commits cmd3 (count=3, == cap) → CapReached fires; cmd4 discarded.
        for (uint i = 1; i <= 4; i++)
        {
            PushCommand(session, i);
        }

        Assert.Equal(1, capCount);
    }

    [Fact]
    public async Task CapReached_FiresAtMostOnce_EvenIfMoreStepsPushed()
    {
        var svc = CreateService(maxSteps: 2);
        await svc.StartRecordingAsync();
        RecordingSession session = svc.CurrentSession!;

        int capCount = 0;
        session.CapReached += (_, _) => capCount++;

        for (uint i = 1; i <= 6; i++)
        {
            PushCommand(session, i);
        }

        Assert.Equal(1, capCount);
    }

    [Fact]
    public async Task StepCountChanged_FiresPerCommittedStep()
    {
        var svc = CreateService(); // no cap
        await svc.StartRecordingAsync();
        RecordingSession session = svc.CurrentSession!;

        var counts = new List<int>();
        session.StepCountChanged += (_, count) => counts.Add(count);

        // 4 commands: cmd1 → pending (no commit event); cmd2 commits cmd1;
        // cmd3 commits cmd2; cmd4 commits cmd3 → events: 1, 2, 3.
        for (uint i = 1; i <= 4; i++)
        {
            PushCommand(session, i);
        }

        Assert.Equal(new[] { 1, 2, 3 }, counts);
    }

    [Fact]
    public async Task MaxStepsIntMaxValue_NeverFiresCap()
    {
        var svc = CreateService(); // default int.MaxValue
        await svc.StartRecordingAsync();
        RecordingSession session = svc.CurrentSession!;

        bool capFired = false;
        session.CapReached += (_, _) => capFired = true;

        for (uint i = 1; i <= 100; i++)
        {
            PushCommand(session, i);
        }

        Assert.False(capFired);
    }

    [Fact]
    public async Task DrainAndStop_AfterCap_ReturnsExactlyCapSteps()
    {
        const int maxSteps = 3;
        var svc = CreateService(maxSteps);
        await svc.StartRecordingAsync();
        RecordingSession session = svc.CurrentSession!;

        // Push 4 commands → 3 committed, cap fires, cmd4 discarded.
        for (uint i = 1; i <= 4; i++)
        {
            PushCommand(session, i);
        }

        var steps = session.DrainAndStop();
        Assert.Equal(maxSteps, steps.Count);
    }

    [Fact]
    public async Task MaxSteps_Property_MatchesConstructorArgument()
    {
        var svc = CreateService(maxSteps: 42);
        await svc.StartRecordingAsync();
        RecordingSession session = svc.CurrentSession!;

        Assert.Equal(42, session.MaxSteps);
    }

    [Fact]
    public async Task DefaultMaxSteps_IsIntMaxValue()
    {
        // When constructed without a maxStepsProvider the engine default is int.MaxValue.
        var svc = CreateService();
        await svc.StartRecordingAsync();
        RecordingSession session = svc.CurrentSession!;

        Assert.Equal(int.MaxValue, session.MaxSteps);
    }
}
