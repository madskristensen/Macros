using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine;
using Macros.Engine.Recording;
using Microsoft.VisualStudio.Threading;
using Xunit;

namespace Macros.Tests.Recording;

/// <summary>
/// Unit tests for <see cref="RecordingSession"/> and the supporting <see cref="ReplayGuard"/>.
/// These exercise the engine in isolation — no VS shell, no command target, no DTE — so a
/// regression in capture semantics fails here long before it could break a real recording.
/// </summary>
public sealed class RecordingSessionTests
{
    private static MacroService CreateService()
    {
        // VSSDK005 normally insists on ThreadHelper.JoinableTaskContext, but that singleton
        // only exists inside a hosted VS process. These are pure engine unit tests with no
        // VS shell, so we own the JoinableTaskContext for the test's lifetime.
#pragma warning disable VSSDK005
        var ctx = new JoinableTaskContext();
#pragma warning restore VSSDK005
        return new MacroService(ctx.Factory);
    }

    private static readonly Guid SampleGroup = new("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void NewSession_OnIdleService_IsNotCapturing()
    {
        var svc = CreateService();
        // Construct a session manually — IdleService means IsCapturing must be false even
        // though the session itself is brand new.
        var session = new RecordingSession(svc);

        Assert.False(session.IsCapturing);
        Assert.Empty(session.Steps);
    }

    [Fact]
    public async Task ServiceRecording_NoReplay_IsCapturing()
    {
        var svc = CreateService();
        await svc.StartRecordingAsync();

        Assert.NotNull(svc.CurrentSession);
        Assert.True(svc.CurrentSession!.IsCapturing);
    }

    [Fact]
    public async Task OnCommand_WhileCapturing_AppendsStep()
    {
        var svc = CreateService();
        await svc.StartRecordingAsync();
        var session = svc.CurrentSession!;

        session.OnCommand(SampleGroup, 42, "Edit.Copy");
        session.OnCommand(SampleGroup, 43, null);

        Assert.Equal(2, session.Count);
        var steps = session.Steps;
        var first = Assert.IsType<RecordedStep.CommandStep>(steps[0]);
        Assert.Equal(SampleGroup, first.Group);
        Assert.Equal(42u, first.Id);
        Assert.Equal("Edit.Copy", first.Name);

        var second = Assert.IsType<RecordedStep.CommandStep>(steps[1]);
        Assert.Equal(43u, second.Id);
        Assert.Null(second.Name);
    }

    [Fact]
    public void OnCommand_WhenNotCapturing_IsNoOp()
    {
        var svc = CreateService();
        var session = new RecordingSession(svc);
        Assert.False(session.IsCapturing);

        session.OnCommand(SampleGroup, 1, "Edit.Copy");
        session.OnCommand(SampleGroup, 2, "Edit.Paste");

        Assert.Empty(session.Steps);
    }

    [Fact]
    public async Task OnCommand_DuringReplay_IsDropped()
    {
        var svc = CreateService();
        await svc.StartRecordingAsync();
        var session = svc.CurrentSession!;

        using (ReplayGuard.Enter())
        {
            Assert.False(session.IsCapturing); // replay guard wins over Recording state
            session.OnCommand(SampleGroup, 99, "Should.NotBeRecorded");
        }

        Assert.Empty(session.Steps);
        Assert.True(session.IsCapturing); // restored after disposal
    }

    [Fact]
    public async Task StopRecording_ClearsCurrentSession()
    {
        var svc = CreateService();
        await svc.StartRecordingAsync();
        Assert.NotNull(svc.CurrentSession);

        await svc.StopRecordingAsync();

        Assert.Null(svc.CurrentSession);
    }

    [Fact]
    public async Task CancelFromRecording_ClearsCurrentSession()
    {
        var svc = CreateService();
        await svc.StartRecordingAsync();
        Assert.NotNull(svc.CurrentSession);

        await svc.CancelAsync();

        Assert.Null(svc.CurrentSession);
    }

    [Fact]
    public void ReplayGuard_EnterFlipsIsReplaying_DisposeReverts()
    {
        Assert.False(ReplayGuard.IsReplaying);

        using (ReplayGuard.Enter())
        {
            Assert.True(ReplayGuard.IsReplaying);
        }

        Assert.False(ReplayGuard.IsReplaying);
    }

    [Fact]
    public void ReplayGuard_NestedEnter_StaysReplayingAfterOneDispose()
    {
        Assert.False(ReplayGuard.IsReplaying);

        var outer = ReplayGuard.Enter();
        var inner = ReplayGuard.Enter();
        Assert.True(ReplayGuard.IsReplaying);

        inner.Dispose();
        Assert.True(ReplayGuard.IsReplaying); // outer scope still holds it

        outer.Dispose();
        Assert.False(ReplayGuard.IsReplaying);
    }

    [Fact]
    public void ReplayGuard_DisposeMoreThanEntered_SaturatesAtZero()
    {
        // Defensive: a future caller that double-disposes shouldn't drive the depth negative
        // and accidentally suppress a *later* Enter scope.
        var scope = ReplayGuard.Enter();
        scope.Dispose();
        scope.Dispose();
        scope.Dispose();

        Assert.False(ReplayGuard.IsReplaying);

        using (ReplayGuard.Enter())
        {
            Assert.True(ReplayGuard.IsReplaying);
        }
    }

    [Fact]
    public async Task ReplayGuard_IsReplaying_FlowsToChildThread()
    {
        // AsyncLocal (unlike [ThreadStatic]) propagates a copy of the execution context into
        // child threads via Thread.Start's ExecutionContext capture. A thread started INSIDE an
        // Enter scope therefore inherits the replaying state — the opposite of [ThreadStatic].
        Assert.False(ReplayGuard.IsReplaying);

        bool seenInChild = false;
        using (ReplayGuard.Enter())
        {
            Assert.True(ReplayGuard.IsReplaying);

            // Thread captures ExecutionContext at Start() time, which is inside the Enter scope.
            var t = new Thread(() => seenInChild = ReplayGuard.IsReplaying);
            t.Start();
            t.Join();
        }

        Assert.True(seenInChild); // AsyncLocal flows; [ThreadStatic] would have been false.
        Assert.False(ReplayGuard.IsReplaying);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task ReplayGuard_IsReplaying_FlowsAcrossAwait()
    {
        using (ReplayGuard.Enter())
        {
            Assert.True(ReplayGuard.IsReplaying);

            // Yield forces the continuation onto the thread-pool scheduler; AsyncLocal
            // preserves the depth across the context switch.
            await Task.Yield();
            Assert.True(ReplayGuard.IsReplaying);

            // Task.Run captures the current ExecutionContext, which holds depth=1.
            bool seenInRun = await Task.Run(() => ReplayGuard.IsReplaying);
            Assert.True(seenInRun);
        }

        Assert.False(ReplayGuard.IsReplaying);
    }

    [Fact]
    public async Task OnFileOpen_WhileCapturing_AppendsFileOpenStep()
    {
        var svc = CreateService();
        await svc.StartRecordingAsync();
        var session = svc.CurrentSession!;

        session.OnFileOpen(@"C:\projects\MyFile.cs");

        Assert.Equal(1, session.Count);
        var steps = session.DrainAndStop();
        var only = Assert.IsType<RecordedStep.FileOpenStep>(steps[0]);
        Assert.Equal(@"C:\projects\MyFile.cs", only.Path);
    }

    [Fact]
    public void OnFileOpen_WhenNotCapturing_IsNoOp()
    {
        var svc = CreateService();
        var session = new RecordingSession(svc);
        Assert.False(session.IsCapturing);

        session.OnFileOpen(@"C:\projects\MyFile.cs");

        Assert.Empty(session.Steps);
    }

    [Fact]
    public async Task OnFileOpen_EmptyOrNullPath_IsNoOp()
    {
        var svc = CreateService();
        await svc.StartRecordingAsync();
        var session = svc.CurrentSession!;

        session.OnFileOpen("");
        session.OnFileOpen(null!);

        Assert.Equal(0, session.Count);
    }

    [Fact]
    public async Task OnFileOpen_NonRootedOrMonikerPath_IsRejected()
    {
        // Regression: VS DocumentEvents.Opened can surface pseudo-document monikers like
        // "RDT_Mk.Solution". Recording these and emitting OpenFileAsync(@"RDT_Mk.Solution")
        // crashes the macro at replay with E_INVALIDARG. The session must filter such
        // inputs before they ever land in the step list.
        var svc = CreateService();
        await svc.StartRecordingAsync();
        var session = svc.CurrentSession!;

        session.OnFileOpen("RDT_Mk.Solution");      // RDT moniker — no separator, not rooted
        session.OnFileOpen("just-a-name.cs");       // bare name, not absolute
        session.OnFileOpen("relative/path.cs");     // relative path
        session.OnFileOpen("https://example.com/file.cs"); // URL
        session.OnFileOpen("   ");                  // whitespace

        Assert.Equal(0, session.Count);
    }

    [Fact]
    public async Task OnFileOpen_RootedPath_IsAccepted()
    {
        var svc = CreateService();
        await svc.StartRecordingAsync();
        var session = svc.CurrentSession!;

        session.OnFileOpen(@"C:\projects\MyFile.cs");

        var steps = session.DrainAndStop();
        Assert.Single(steps);
        Assert.IsType<RecordedStep.FileOpenStep>(steps[0]);
    }

    [Fact]
    public async Task OnFileClose_RootedPath_IsAccepted_AndEmitsFileCloseStep()
    {
        var svc = CreateService();
        await svc.StartRecordingAsync();
        var session = svc.CurrentSession!;

        session.OnFileClose(@"C:\projects\MyFile.cs");

        var steps = session.DrainAndStop();
        var step = Assert.Single(steps);
        var close = Assert.IsType<RecordedStep.FileCloseStep>(step);
        Assert.Equal(@"C:\projects\MyFile.cs", close.Path);
    }

    [Fact]
    public async Task OnFileClose_NonRootedOrEmptyPath_IsRejected()
    {
        var svc = CreateService();
        await svc.StartRecordingAsync();
        var session = svc.CurrentSession!;

        session.OnFileClose("");
        session.OnFileClose(null!);
        session.OnFileClose("RDT_Mk.Solution");
        session.OnFileClose("relative/path.cs");

        Assert.Equal(0, session.Count);
    }

    [Fact]
    public async Task OnToolWindowClose_NonEmptyCaption_IsAccepted_AndEmitsStep()
    {
        var svc = CreateService();
        await svc.StartRecordingAsync();
        var session = svc.CurrentSession!;

        session.OnToolWindowClose("Server Explorer");

        var steps = session.DrainAndStop();
        var step = Assert.Single(steps);
        var tw = Assert.IsType<RecordedStep.ToolWindowClosedStep>(step);
        Assert.Equal("Server Explorer", tw.Caption);
    }

    [Fact]
    public async Task OnToolWindowClose_EmptyOrNull_IsNoOp()
    {
        var svc = CreateService();
        await svc.StartRecordingAsync();
        var session = svc.CurrentSession!;

        session.OnToolWindowClose("");
        session.OnToolWindowClose(null!);

        Assert.Equal(0, session.Count);
    }

    [Fact]
    public async Task OnFileOpen_FlushesAggregatorPending_BeforeRecordingFileOpen()
    {
        // A pending CommandStep in the aggregator should be committed when a
        // FileOpenStep arrives (aggregator rule: non-TextEditStep always flushes pending).
        var svc = CreateService();
        await svc.StartRecordingAsync();
        var session = svc.CurrentSession!;

        session.OnCommand(SampleGroup, 1, "Edit.Copy");
        session.OnFileOpen(@"C:\projects\MyFile.cs");

        // After a FileOpenStep pushes through, the pending CommandStep is flushed.
        // DrainAndStop flushes the FileOpenStep itself.
        var steps = session.DrainAndStop();
        Assert.Equal(2, steps.Count);
        Assert.IsType<RecordedStep.CommandStep>(steps[0]);
        Assert.IsType<RecordedStep.FileOpenStep>(steps[1]);
    }

    [Fact]
    public async Task ReplayGuard_IsReplaying_DoesNotLeakToParallelChain()
    {
        // Both tasks are started from the current context (depth=0). Task A enters its own
        // scope; Task B does not. Because AsyncLocal copies on fork, Task A's Enter does not
        // bleed into Task B's isolated copy of the context.
        bool taskAResult = false;
        bool taskBResult = true; // will be set to false inside Task B

        await Task.WhenAll(
            Task.Run(async () =>
            {
                using (ReplayGuard.Enter())
                {
                    taskAResult = ReplayGuard.IsReplaying; // true inside Enter
                    await Task.Yield();
                    taskAResult = taskAResult && ReplayGuard.IsReplaying;
                }
            }),
            Task.Run(() =>
            {
                // No Enter here — parent depth was 0 when this task was forked.
                taskBResult = ReplayGuard.IsReplaying;
            }));

        Assert.True(taskAResult);
        Assert.False(taskBResult);
    }
}
