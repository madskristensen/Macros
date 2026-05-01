using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Macros.Engine.Triggers;
using Xunit;

namespace Macros.Tests.Triggers;

/// <summary>
/// Pins the contract of <see cref="MacroFailureTracker"/>: threshold semantics, streak reset
/// on success, re-enable, event firing, custom threshold, snapshot, and thread-safety.
/// </summary>
public sealed class MacroFailureTrackerTests
{
    private const string Path1 = @"X:\macros\Foo.csx";
    private const string Path2 = @"X:\macros\Bar.csx";

    [Fact]
    public void New_Tracker_IsAutoDisabled_ReturnsFalse_ForAnyPath()
    {
        var tracker = new MacroFailureTracker();

        Assert.False(tracker.IsAutoDisabled(Path1));
        Assert.False(tracker.IsAutoDisabled("some-random-path"));
        Assert.Empty(tracker.GetDisabledPaths());
    }

    [Fact]
    public void RecordSuccess_OnUnknownPath_IsNoOp_DoesNotThrow()
    {
        var tracker = new MacroFailureTracker();

        // Must not throw and must leave state clean.
        tracker.RecordSuccess("X:\\unknown\\Missing.csx");
        Assert.Empty(tracker.GetDisabledPaths());
    }

    [Fact]
    public void RecordFailure_OnceAndTwice_ReturnsFalse()
    {
        var tracker = new MacroFailureTracker(threshold: 3);

        Assert.False(tracker.RecordFailure(Path1)); // 1st
        Assert.False(tracker.RecordFailure(Path1)); // 2nd
        Assert.False(tracker.IsAutoDisabled(Path1));
    }

    [Fact]
    public void RecordFailure_ThirdTime_ReturnsTrue()
    {
        var tracker = new MacroFailureTracker(threshold: 3);

        tracker.RecordFailure(Path1);
        tracker.RecordFailure(Path1);
        var result = tracker.RecordFailure(Path1); // 3rd — hits threshold

        Assert.True(result);
    }

    [Fact]
    public void After_AutoDisable_IsAutoDisabled_ReturnsTrue()
    {
        var tracker = new MacroFailureTracker(threshold: 3);

        tracker.RecordFailure(Path1);
        tracker.RecordFailure(Path1);
        tracker.RecordFailure(Path1);

        Assert.True(tracker.IsAutoDisabled(Path1));
    }

    [Fact]
    public void RecordSuccess_After_AutoDisable_ResetsAndRemovesFromDisabled()
    {
        var tracker = new MacroFailureTracker(threshold: 3);

        tracker.RecordFailure(Path1);
        tracker.RecordFailure(Path1);
        tracker.RecordFailure(Path1);
        Assert.True(tracker.IsAutoDisabled(Path1));

        tracker.RecordSuccess(Path1);

        Assert.False(tracker.IsAutoDisabled(Path1));
        Assert.Empty(tracker.GetDisabledPaths());
    }

    [Fact]
    public void RecordFailure_AfterSuccess_RestartsCountFromOne()
    {
        var tracker = new MacroFailureTracker(threshold: 3);

        tracker.RecordFailure(Path1);
        tracker.RecordFailure(Path1);
        tracker.RecordSuccess(Path1); // reset

        Assert.False(tracker.RecordFailure(Path1)); // 1st again
        Assert.False(tracker.RecordFailure(Path1)); // 2nd
        Assert.False(tracker.IsAutoDisabled(Path1));
    }

    [Fact]
    public void ReEnable_RemovesFromDisabled_AndRecordFailure_StartsAtOne()
    {
        var tracker = new MacroFailureTracker(threshold: 3);

        tracker.RecordFailure(Path1);
        tracker.RecordFailure(Path1);
        tracker.RecordFailure(Path1);
        Assert.True(tracker.IsAutoDisabled(Path1));

        tracker.ReEnable(Path1);

        Assert.False(tracker.IsAutoDisabled(Path1));
        Assert.Empty(tracker.GetDisabledPaths());

        // Streak restarted: 1 failure should not re-disable.
        Assert.False(tracker.RecordFailure(Path1));
        Assert.False(tracker.IsAutoDisabled(Path1));
    }

    [Fact]
    public void AutoDisabled_Event_FiresExactlyOnce_AtThreshold()
    {
        var tracker = new MacroFailureTracker(threshold: 3);
        var events = new List<MacroAutoDisabledEventArgs>();
        tracker.AutoDisabled += (_, e) => events.Add(e);

        tracker.RecordFailure(Path1); // 1
        tracker.RecordFailure(Path1); // 2
        tracker.RecordFailure(Path1); // 3 — fires
        tracker.RecordFailure(Path1); // already disabled — no second event

        Assert.Single(events);
        Assert.Equal(Path1, events[0].MacroPath);
        Assert.Equal("Foo", events[0].MacroName);
        Assert.Equal(3, events[0].FailureCount);
    }

    [Fact]
    public void CustomThreshold_Requires_N_Failures()
    {
        var tracker = new MacroFailureTracker(threshold: 5);

        for (var i = 1; i < 5; i++)
        {
            Assert.False(tracker.RecordFailure(Path1), $"Failure {i} should not yet disable.");
        }
        Assert.True(tracker.RecordFailure(Path1), "5th failure should disable.");
        Assert.True(tracker.IsAutoDisabled(Path1));
    }

    [Fact]
    public void GetDisabledPaths_Returns_Snapshot_List()
    {
        var tracker = new MacroFailureTracker(threshold: 1);
        tracker.RecordFailure(Path1);
        tracker.RecordFailure(Path2);

        var snapshot = tracker.GetDisabledPaths();

        Assert.Equal(2, snapshot.Count);
        Assert.Contains(Path1, snapshot);
        Assert.Contains(Path2, snapshot);

        // Snapshot is independent: re-enable should not mutate previous snapshot.
        tracker.ReEnable(Path1);
        Assert.Equal(2, snapshot.Count); // unchanged
    }

    [Fact]
    public async Task ThreadSafety_50_Parallel_RecordFailures_ProduceOneAutoDisable()
    {
        var tracker = new MacroFailureTracker(threshold: 3);
        var autoDisableCount = 0;
        tracker.AutoDisabled += (_, _) => System.Threading.Interlocked.Increment(ref autoDisableCount);

        var tasks = new Task[50];
        for (var i = 0; i < 50; i++)
        {
            tasks[i] = Task.Run(() => tracker.RecordFailure(Path1));
        }
        await Task.WhenAll(tasks);

        // The macro must be disabled after 50 calls on threshold=3.
        Assert.True(tracker.IsAutoDisabled(Path1));

        // AutoDisabled must have fired exactly once (the first time it crossed the threshold).
        Assert.Equal(1, autoDisableCount);
    }
}
