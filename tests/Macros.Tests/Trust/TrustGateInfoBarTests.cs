using System;
using System.Collections.Generic;
using Macros.Engine.Storage;
using Macros.Engine.Triggers;
using Macros.Trust;
using Xunit;

namespace Macros.Tests.Trust;

/// <summary>
/// Decision-table tests for <see cref="TrustGateLogic.ShouldShow"/>. The full
/// <see cref="TrustGateInfoBar"/> wraps these inputs around <see cref="VS.InfoBar"/> /
/// <see cref="Macros.Options.MacrosOptions"/> / a <c>SolutionContextTracker</c>, all of
/// which require a hosted VS shell. The decision itself is a pure function of four
/// inputs and is therefore the unit under test.
/// </summary>
public sealed class TrustGateInfoBarTests
{
    private static readonly DateTimeOffset Stamp =
        new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ShouldShow_NullSolutionPath_ReturnsFalse()
    {
        var (show, count) = TrustGateLogic.ShouldShow(
            solutionPath: null,
            isTrusted: false,
            isBlocked: false,
            repoMacros: new[] { Triggered("A") });

        Assert.False(show);
        Assert.Equal(0, count);
    }

    [Fact]
    public void ShouldShow_AlreadyTrusted_ReturnsFalse()
    {
        var (show, count) = TrustGateLogic.ShouldShow(
            solutionPath: @"C:\repo",
            isTrusted: true,
            isBlocked: false,
            repoMacros: new[] { Triggered("A"), Triggered("B") });

        Assert.False(show);
        Assert.Equal(0, count);
    }

    [Fact]
    public void ShouldShow_AlreadyBlocked_ReturnsFalse()
    {
        var (show, count) = TrustGateLogic.ShouldShow(
            solutionPath: @"C:\repo",
            isTrusted: false,
            isBlocked: true,
            repoMacros: new[] { Triggered("A") });

        Assert.False(show);
        Assert.Equal(0, count);
    }

    [Fact]
    public void ShouldShow_EmptyRepo_ReturnsFalse()
    {
        var (show, count) = TrustGateLogic.ShouldShow(
            solutionPath: @"C:\repo",
            isTrusted: false,
            isBlocked: false,
            repoMacros: Array.Empty<MacroEntry>());

        Assert.False(show);
        Assert.Equal(0, count);
    }

    [Fact]
    public void ShouldShow_OnlyManualTriggers_ReturnsFalse()
    {
        var (show, count) = TrustGateLogic.ShouldShow(
            solutionPath: @"C:\repo",
            isTrusted: false,
            isBlocked: false,
            repoMacros: new[] { Manual("A"), Manual("B"), Manual("C") });

        Assert.False(show);
        Assert.Equal(0, count);
    }

    [Fact]
    public void ShouldShow_TwoTriggeredMacros_ReturnsTrueWithCountTwo()
    {
        var (show, count) = TrustGateLogic.ShouldShow(
            solutionPath: @"C:\repo",
            isTrusted: false,
            isBlocked: false,
            repoMacros: new[] { Triggered("A"), Triggered("B") });

        Assert.True(show);
        Assert.Equal(2, count);
    }

    [Fact]
    public void ShouldShow_MixOfManualAndTriggered_OnlyCountsTriggered()
    {
        var (show, count) = TrustGateLogic.ShouldShow(
            solutionPath: @"C:\repo",
            isTrusted: false,
            isBlocked: false,
            repoMacros: new[]
            {
                Manual("A"),
                Triggered("B"),
                Manual("C"),
                Triggered("D"),
                Triggered("E"),
            });

        Assert.True(show);
        Assert.Equal(3, count);
    }

    [Fact]
    public void ShouldShow_MacroWithMultipleTriggersIncludingManual_CountsOnce()
    {
        // A single macro carrying both Manual and a non-Manual trigger should still
        // count as exactly one triggered macro — ShouldShow filters by macro, not by
        // binding.
        var entry = new MacroEntry(
            Name: "Mixed",
            Scope: MacroScope.Repo,
            Path: @"C:\repo\.vs\Macros\Mixed.csx",
            StepCount: 1,
            Modified: Stamp,
            SizeBytes: 64,
            Triggers: new[]
            {
                TriggerBinding.Manual,
                new TriggerBinding(TriggerKind.AfterCommand, "File.Save"),
            });

        var (show, count) = TrustGateLogic.ShouldShow(
            solutionPath: @"C:\repo",
            isTrusted: false,
            isBlocked: false,
            repoMacros: new[] { entry });

        Assert.True(show);
        Assert.Equal(1, count);
    }

    [Fact]
    public void ShouldShow_NullTriggersOnEntry_DoesNotThrow_AndIsNotCounted()
    {
        var entry = new MacroEntry(
            Name: "NoTriggers",
            Scope: MacroScope.Repo,
            Path: @"C:\repo\.vs\Macros\NoTriggers.csx",
            StepCount: 0,
            Modified: Stamp,
            SizeBytes: 1,
            Triggers: null!);

        var (show, count) = TrustGateLogic.ShouldShow(
            solutionPath: @"C:\repo",
            isTrusted: false,
            isBlocked: false,
            repoMacros: new[] { entry });

        Assert.False(show);
        Assert.Equal(0, count);
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static MacroEntry Manual(string name) => new(
        Name: name,
        Scope: MacroScope.Repo,
        Path: $@"C:\repo\.vs\Macros\{name}.csx",
        StepCount: 0,
        Modified: Stamp,
        SizeBytes: 16,
        Triggers: new[] { TriggerBinding.Manual });

    private static MacroEntry Triggered(string name) => new(
        Name: name,
        Scope: MacroScope.Repo,
        Path: $@"C:\repo\.vs\Macros\{name}.csx",
        StepCount: 1,
        Modified: Stamp,
        SizeBytes: 32,
        Triggers: new List<TriggerBinding>
        {
            new TriggerBinding(TriggerKind.VsEvent, "Build.SolutionBuildDone"),
        });
}
