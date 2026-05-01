using System;
using System.Threading.Tasks;
using Macros.Engine;
using Macros.Engine.Recording;
using Microsoft.VisualStudio.Threading;
using Xunit;

namespace Macros.Tests.Recording;

/// <summary>
/// Unit tests for <see cref="StepAggregator"/> and the new <see cref="RecordingSession"/>
/// integration surface (<c>DrainAndStop</c>). The aggregator is pure data — no VS dependencies —
/// so we can drive every merge rule deterministically by passing an explicit
/// <see cref="DateTime"/> to <see cref="StepAggregator.Push"/>.
/// </summary>
public sealed class StepAggregatorTests
{
    private static readonly DateTime T0 = new(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid SampleGroup = new("11111111-2222-3333-4444-555555555555");

    private static MacroService CreateService()
    {
#pragma warning disable VSSDK005
        var ctx = new JoinableTaskContext();
#pragma warning restore VSSDK005
        return new MacroService(ctx.Factory);
    }

    private static TextEditStep Insert(int position, string text) =>
        new(position, OldLength: 0, OldText: string.Empty, NewText: text);

    private static TextEditStep Delete(int position, string removed) =>
        new(position, OldLength: removed.Length, OldText: removed, NewText: string.Empty);

    [Fact]
    public void Flush_OnEmpty_ReturnsNull()
    {
        var agg = new StepAggregator();

        Assert.Null(agg.Flush());
        Assert.False(agg.HasPending);
    }

    [Fact]
    public void Push_SingleStep_FlushReturnsIt()
    {
        var agg = new StepAggregator();

        var finalized = agg.Push(Insert(0, "a"), T0);

        Assert.Null(finalized);
        Assert.True(agg.HasPending);

        var drained = agg.Flush();
        Assert.NotNull(drained);
        var step = Assert.IsType<TextEditStep>(drained!);
        Assert.Equal("a", step.NewText);
    }

    [Fact]
    public void Push_TwoAdjacentInsertions_MergesIntoOne()
    {
        var agg = new StepAggregator();

        Assert.Null(agg.Push(Insert(5, "a"), T0));
        Assert.Null(agg.Push(Insert(6, "b"), T0.AddMilliseconds(50)));

        var drained = Assert.IsType<TextEditStep>(agg.Flush()!);
        Assert.Equal(5, drained.OldPosition);
        Assert.Equal(0, drained.OldLength);
        Assert.Equal(string.Empty, drained.OldText);
        Assert.Equal("ab", drained.NewText);
    }

    [Fact]
    public void Push_LongTypingRun_MergesIntoOneInsertion()
    {
        var agg = new StepAggregator();
        const string typed = "hello, world";

        for (int i = 0; i < typed.Length; i++)
        {
            // Each Push must merge into the running pending — never finalise mid-run.
            Assert.Null(agg.Push(Insert(i, typed[i].ToString()), T0.AddMilliseconds(i * 10)));
        }

        var drained = Assert.IsType<TextEditStep>(agg.Flush()!);
        Assert.Equal(typed, drained.NewText);
    }

    [Fact]
    public void Push_NonAdjacentInsertions_FlushesPrevious()
    {
        var agg = new StepAggregator();

        Assert.Null(agg.Push(Insert(0, "a"), T0));

        // Position 100 is nowhere near pending's tail (1) — caret jumped.
        var finalized = agg.Push(Insert(100, "b"), T0.AddMilliseconds(20));
        var first = Assert.IsType<TextEditStep>(finalized!);
        Assert.Equal("a", first.NewText);

        var drained = Assert.IsType<TextEditStep>(agg.Flush()!);
        Assert.Equal("b", drained.NewText);
        Assert.Equal(100, drained.OldPosition);
    }

    [Fact]
    public void Push_TextEditThenCommand_FlushesTextEdit()
    {
        var agg = new StepAggregator();
        Assert.Null(agg.Push(Insert(0, "a"), T0));

        var cmd = new RecordedStep.CommandStep(SampleGroup, 42, "Edit.Copy");
        var finalized = agg.Push(cmd, T0.AddMilliseconds(5));

        var flushed = Assert.IsType<TextEditStep>(finalized!);
        Assert.Equal("a", flushed.NewText);

        // CommandStep itself is now pending; Flush drains it.
        var drained = Assert.IsType<RecordedStep.CommandStep>(agg.Flush()!);
        Assert.Equal(42u, drained.Id);
    }

    [Fact]
    public void Push_TwoCommands_DoesNotMerge()
    {
        var agg = new StepAggregator();
        var c1 = new RecordedStep.CommandStep(SampleGroup, 1, "Edit.Copy");
        var c2 = new RecordedStep.CommandStep(SampleGroup, 2, "Edit.Paste");

        Assert.Null(agg.Push(c1, T0));
        var finalized = Assert.IsType<RecordedStep.CommandStep>(agg.Push(c2, T0)!);
        Assert.Equal(1u, finalized.Id);

        var drained = Assert.IsType<RecordedStep.CommandStep>(agg.Flush()!);
        Assert.Equal(2u, drained.Id);
    }

    [Fact]
    public void Push_CommandThenAdjacentTextEdit_DoesNotMerge()
    {
        var agg = new StepAggregator();
        var cmd = new RecordedStep.CommandStep(SampleGroup, 7, "Edit.Whatever");

        Assert.Null(agg.Push(cmd, T0));
        var finalized = agg.Push(Insert(0, "a"), T0.AddMilliseconds(10));

        // The command flushes; the insert becomes pending.
        Assert.IsType<RecordedStep.CommandStep>(finalized!);
        var drained = Assert.IsType<TextEditStep>(agg.Flush()!);
        Assert.Equal("a", drained.NewText);
    }

    [Fact]
    public void Push_AdjacentBackspaces_MergeIntoExtendedDeletion()
    {
        var agg = new StepAggregator();

        // Caret at position 5, user deletes 'd' (was at index 4).
        Assert.Null(agg.Push(Delete(4, "d"), T0));
        // Then deletes 'c' (was at index 3) — adjacent backspace.
        Assert.Null(agg.Push(Delete(3, "c"), T0.AddMilliseconds(50)));
        // Then 'b'.
        Assert.Null(agg.Push(Delete(2, "b"), T0.AddMilliseconds(100)));

        var drained = Assert.IsType<TextEditStep>(agg.Flush()!);
        Assert.Equal(2, drained.OldPosition);
        Assert.Equal(3, drained.OldLength);
        Assert.Equal("bcd", drained.OldText);
        Assert.Equal(string.Empty, drained.NewText);
    }

    [Fact]
    public void Push_AdjacentForwardDeletes_MergeIntoExtendedDeletion()
    {
        var agg = new StepAggregator();

        // Caret at position 5, user presses Delete: 'a' at index 5 vanishes.
        Assert.Null(agg.Push(Delete(5, "a"), T0));
        // Caret stays at 5; next Delete consumes 'b' (now at index 5 post-shift, but
        // Delete-forward reports the pre-change OldPosition the same way — it stays at 5).
        Assert.Null(agg.Push(Delete(5, "b"), T0.AddMilliseconds(50)));

        var drained = Assert.IsType<TextEditStep>(agg.Flush()!);
        Assert.Equal(5, drained.OldPosition);
        Assert.Equal(2, drained.OldLength);
        Assert.Equal("ab", drained.OldText);
        Assert.Equal(string.Empty, drained.NewText);
    }

    [Fact]
    public void Push_NonAdjacentDeletions_DoNotMerge()
    {
        var agg = new StepAggregator();

        Assert.Null(agg.Push(Delete(10, "x"), T0));
        var finalized = agg.Push(Delete(20, "y"), T0.AddMilliseconds(50));

        var first = Assert.IsType<TextEditStep>(finalized!);
        Assert.Equal(10, first.OldPosition);

        var drained = Assert.IsType<TextEditStep>(agg.Flush()!);
        Assert.Equal(20, drained.OldPosition);
    }

    [Fact]
    public void Push_InsertionThenDeletion_DoesNotMerge()
    {
        var agg = new StepAggregator();

        Assert.Null(agg.Push(Insert(0, "a"), T0));
        // A deletion can never merge with an insertion — both have to be the same kind.
        var finalized = agg.Push(Delete(0, "a"), T0.AddMilliseconds(20));

        var flushed = Assert.IsType<TextEditStep>(finalized!);
        Assert.Equal("a", flushed.NewText);

        var drained = Assert.IsType<TextEditStep>(agg.Flush()!);
        Assert.Equal("a", drained.OldText);
        Assert.Equal(string.Empty, drained.NewText);
    }

    [Fact]
    public void Push_ReplacementEdit_DoesNotMergeWithAdjacentInsert()
    {
        var agg = new StepAggregator();

        // Replacement: OldLength > 0 AND NewText != "" — neither rule applies.
        var replacement = new TextEditStep(0, OldLength: 1, OldText: "a", NewText: "Z");
        Assert.Null(agg.Push(replacement, T0));

        var finalized = agg.Push(Insert(1, "b"), T0.AddMilliseconds(10));

        // Replacement should have been flushed verbatim; the insert is pending.
        var flushed = Assert.IsType<TextEditStep>(finalized!);
        Assert.Equal("Z", flushed.NewText);
        Assert.Equal("a", flushed.OldText);

        var drained = Assert.IsType<TextEditStep>(agg.Flush()!);
        Assert.Equal("b", drained.NewText);
    }

    [Fact]
    public void Push_AfterTimeGap_FlushesEvenIfMergeable()
    {
        var agg = new StepAggregator(TimeSpan.FromMilliseconds(500));

        Assert.Null(agg.Push(Insert(0, "a"), T0));

        // 600ms later — past the threshold even though positions are adjacent.
        var finalized = agg.Push(Insert(1, "b"), T0.AddMilliseconds(600));

        var flushed = Assert.IsType<TextEditStep>(finalized!);
        Assert.Equal("a", flushed.NewText);

        var drained = Assert.IsType<TextEditStep>(agg.Flush()!);
        Assert.Equal("b", drained.NewText);
    }

    [Fact]
    public void Push_ExactlyAtTimeThreshold_StillMerges()
    {
        var agg = new StepAggregator(TimeSpan.FromMilliseconds(500));

        Assert.Null(agg.Push(Insert(0, "a"), T0));
        // 500ms is the boundary; <= keeps it mergeable so users don't lose runs by a single tick.
        Assert.Null(agg.Push(Insert(1, "b"), T0.AddMilliseconds(500)));

        var drained = Assert.IsType<TextEditStep>(agg.Flush()!);
        Assert.Equal("ab", drained.NewText);
    }

    [Fact]
    public void Reset_DropsPendingWithoutReturning()
    {
        var agg = new StepAggregator();
        Assert.Null(agg.Push(Insert(0, "a"), T0));
        Assert.True(agg.HasPending);

        agg.Reset();

        Assert.False(agg.HasPending);
        Assert.Null(agg.Flush());
    }

    [Fact]
    public void Push_NullStep_Throws()
    {
        var agg = new StepAggregator();
        Assert.Throws<ArgumentNullException>(() => agg.Push(null!, T0));
    }

    // --- RecordingSession.DrainAndStop integration ------------------------------------------

    [Fact]
    public async Task DrainAndStop_CoalescesTypingRunWithCommandsInOrder()
    {
        var svc = CreateService();
        await svc.StartRecordingAsync();
        var session = svc.CurrentSession!;

        // Sequence: command, type "ab" (two adjacent inserts → one merged step), command,
        // type "c" (single insert → one step).  Expected committed list = 4 steps total.
        session.OnCommand(SampleGroup, 1, "Edit.Copy");
        session.OnTextEdit(new TextEditStep(0, 0, string.Empty, "a"));
        session.OnTextEdit(new TextEditStep(1, 0, string.Empty, "b"));
        session.OnCommand(SampleGroup, 2, "Edit.Paste");
        session.OnTextEdit(new TextEditStep(2, 0, string.Empty, "c"));

        var steps = session.DrainAndStop();

        Assert.Equal(4, steps.Count);
        Assert.IsType<RecordedStep.CommandStep>(steps[0]);
        var typed = Assert.IsType<TextEditStep>(steps[1]);
        Assert.Equal("ab", typed.NewText);
        Assert.IsType<RecordedStep.CommandStep>(steps[2]);
        var lastTyped = Assert.IsType<TextEditStep>(steps[3]);
        Assert.Equal("c", lastTyped.NewText);
    }

    [Fact]
    public async Task DrainAndStop_WithNoEvents_ReturnsEmpty()
    {
        var svc = CreateService();
        await svc.StartRecordingAsync();
        var session = svc.CurrentSession!;

        var steps = session.DrainAndStop();

        Assert.Empty(steps);
    }

    [Fact]
    public async Task DrainAndStop_AfterStop_DropsCapture()
    {
        var svc = CreateService();
        await svc.StartRecordingAsync();
        var session = svc.CurrentSession!;

        session.OnTextEdit(new TextEditStep(0, 0, string.Empty, "a"));
        session.OnTextEdit(new TextEditStep(1, 0, string.Empty, "b"));

        // Real flow: MacroService.StopRecordingAsync transitions state, THEN DrainAndStop runs
        // — IsCapturing is false by the time we drain, so no concurrent push can race.
        var src = await svc.StopRecordingAsync();

        // The generator's header reflects the coalesced count (one merged "ab" step).
        Assert.Contains("// Steps: 1", src);
        Assert.Contains("await TypeAsync(\"ab\");", src);
        Assert.False(session.IsCapturing);
        Assert.Null(svc.CurrentSession);
    }
}
