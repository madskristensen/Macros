using System;
using System.Collections.Generic;
using Macros.Engine.Storage;
using Macros.Engine.Triggers;
using Macros.Options;
using Macros.Trust;
using Xunit;

namespace Macros.Tests.Integration;

/// <summary>
/// Integration tests for the trust-gate logic combining a real <see cref="MacrosOptions"/>
/// instance (in-memory, not persisted) with <see cref="TrustGateLogic.ShouldShow"/>.
/// These tests exercise the full trust / block decision pipeline without requiring a VS
/// host — <see cref="MacrosOptions.TrustSolution"/>, <see cref="MacrosOptions.BlockSolution"/>,
/// <see cref="MacrosOptions.IsSolutionTrusted"/>, and <see cref="MacrosOptions.IsSolutionBlocked"/>
/// are all pure in-memory operations over string-backed properties.
/// </summary>
public sealed class TrustGateIntegrationTests
{
    private static readonly DateTimeOffset Stamp =
        new(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private const string SolutionPath = @"C:\repos\MySolution.sln";

    // ─── Helpers ──────────────────────────────────────────────────────────────────────

    private static MacroEntry Triggered(string name) => new(
        Name: name,
        Scope: MacroScope.Repo,
        Path: $@"C:\repos\.vs\Macros\{name}.csx",
        StepCount: 1,
        Modified: Stamp,
        SizeBytes: 64,
        Triggers: new[] { new TriggerBinding(TriggerKind.VsEvent, "Build.SolutionBuildDone") });

    private static MacroEntry Manual(string name) => new(
        Name: name,
        Scope: MacroScope.Repo,
        Path: $@"C:\repos\.vs\Macros\{name}.csx",
        StepCount: 0,
        Modified: Stamp,
        SizeBytes: 16,
        Triggers: new[] { TriggerBinding.Manual });

    // ─── 1. Untrusted solution with triggered macros → ShouldShow returns true ─────────

    [Fact]
    public void UntrustedSolution_WithTriggeredMacros_ShouldShow_Returns_True()
    {
        var options = new MacrosOptions();
        var repoMacros = new[] { Triggered("OnBuild"), Triggered("OnSave") };

        var (show, count) = TrustGateLogic.ShouldShow(
            solutionPath: SolutionPath,
            isTrusted: options.IsSolutionTrusted(SolutionPath),
            isBlocked: options.IsSolutionBlocked(SolutionPath),
            repoMacros: repoMacros);

        Assert.True(show);
        Assert.Equal(2, count);
    }

    [Fact]
    public void UntrustedSolution_WithMixedMacros_ShouldShow_Returns_True_CountsOnlyTriggered()
    {
        var options = new MacrosOptions();
        var repoMacros = new[] { Manual("ManualOnly"), Triggered("OnBuild") };

        var (show, count) = TrustGateLogic.ShouldShow(
            solutionPath: SolutionPath,
            isTrusted: options.IsSolutionTrusted(SolutionPath),
            isBlocked: options.IsSolutionBlocked(SolutionPath),
            repoMacros: repoMacros);

        Assert.True(show);
        Assert.Equal(1, count);
    }

    [Fact]
    public void UntrustedSolution_OnlyManualMacros_ShouldShow_Returns_False()
    {
        var options = new MacrosOptions();
        var repoMacros = new[] { Manual("Alpha"), Manual("Beta") };

        var (show, count) = TrustGateLogic.ShouldShow(
            solutionPath: SolutionPath,
            isTrusted: options.IsSolutionTrusted(SolutionPath),
            isBlocked: options.IsSolutionBlocked(SolutionPath),
            repoMacros: repoMacros);

        Assert.False(show);
        Assert.Equal(0, count);
    }

    // ─── 2. After TrustSolution → ShouldShow returns false ────────────────────────────

    [Fact]
    public void AfterTrustSolution_ShouldShow_Returns_False()
    {
        var options = new MacrosOptions();
        var repoMacros = new[] { Triggered("OnBuild"), Triggered("OnSave") };

        // Sanity: untrusted shows the bar.
        var (showBefore, _) = TrustGateLogic.ShouldShow(
            SolutionPath,
            options.IsSolutionTrusted(SolutionPath),
            options.IsSolutionBlocked(SolutionPath),
            repoMacros);
        Assert.True(showBefore);

        // Trust the solution.
        options.TrustSolution(SolutionPath);

        var (showAfter, count) = TrustGateLogic.ShouldShow(
            SolutionPath,
            options.IsSolutionTrusted(SolutionPath),
            options.IsSolutionBlocked(SolutionPath),
            repoMacros);

        Assert.False(showAfter);
        Assert.Equal(0, count);
        Assert.True(options.IsSolutionTrusted(SolutionPath));
    }

    [Fact]
    public void TrustSolution_Different_Solution_Does_Not_Affect_Original()
    {
        var options = new MacrosOptions();
        var otherSolution = @"C:\other\Other.sln";
        var repoMacros = new[] { Triggered("OnBuild") };

        // Trust a DIFFERENT solution.
        options.TrustSolution(otherSolution);

        // Original solution should still show.
        var (show, count) = TrustGateLogic.ShouldShow(
            SolutionPath,
            options.IsSolutionTrusted(SolutionPath),
            options.IsSolutionBlocked(SolutionPath),
            repoMacros);

        Assert.True(show);
        Assert.Equal(1, count);
    }

    // ─── 3. After Block → IsBlocked=true, ShouldShow=false ────────────────────────────

    [Fact]
    public void AfterBlockSolution_IsBlocked_True_ShouldShow_Returns_False()
    {
        var options = new MacrosOptions();
        var repoMacros = new[] { Triggered("OnBuild") };

        options.BlockSolution(SolutionPath);

        Assert.True(options.IsSolutionBlocked(SolutionPath));
        Assert.False(options.IsSolutionTrusted(SolutionPath));

        var (show, count) = TrustGateLogic.ShouldShow(
            SolutionPath,
            options.IsSolutionTrusted(SolutionPath),
            options.IsSolutionBlocked(SolutionPath),
            repoMacros);

        Assert.False(show);
        Assert.Equal(0, count);
    }

    [Fact]
    public void TrustThenBlock_IsBlocked_True_WasNotTrusted_ShouldShow_Returns_False()
    {
        var options = new MacrosOptions();
        var repoMacros = new[] { Triggered("OnBuild") };

        // Trust first, then block.
        options.TrustSolution(SolutionPath);
        Assert.True(options.IsSolutionTrusted(SolutionPath));

        options.BlockSolution(SolutionPath);

        // Blocking should revoke trust automatically.
        Assert.False(options.IsSolutionTrusted(SolutionPath), "Blocking should revoke trust.");
        Assert.True(options.IsSolutionBlocked(SolutionPath));

        var (show, count) = TrustGateLogic.ShouldShow(
            SolutionPath,
            options.IsSolutionTrusted(SolutionPath),
            options.IsSolutionBlocked(SolutionPath),
            repoMacros);

        Assert.False(show);
        Assert.Equal(0, count);
    }

    [Fact]
    public void BlockThenTrust_IsBlocked_Revoked_ShouldShow_Returns_False()
    {
        var options = new MacrosOptions();
        var repoMacros = new[] { Triggered("OnBuild") };

        options.BlockSolution(SolutionPath);
        options.TrustSolution(SolutionPath);

        Assert.True(options.IsSolutionTrusted(SolutionPath));
        Assert.False(options.IsSolutionBlocked(SolutionPath), "Trusting should revoke block.");

        var (show, count) = TrustGateLogic.ShouldShow(
            SolutionPath,
            options.IsSolutionTrusted(SolutionPath),
            options.IsSolutionBlocked(SolutionPath),
            repoMacros);

        Assert.False(show);
        Assert.Equal(0, count);
    }

    [Fact]
    public void RevokeTrust_After_TrustSolution_ShouldShow_Returns_True_Again()
    {
        var options = new MacrosOptions();
        var repoMacros = new[] { Triggered("OnBuild") };

        options.TrustSolution(SolutionPath);
        Assert.False(TrustGateLogic.ShouldShow(
            SolutionPath,
            options.IsSolutionTrusted(SolutionPath),
            options.IsSolutionBlocked(SolutionPath),
            repoMacros).show);

        options.RevokeTrust(SolutionPath);

        var (show, count) = TrustGateLogic.ShouldShow(
            SolutionPath,
            options.IsSolutionTrusted(SolutionPath),
            options.IsSolutionBlocked(SolutionPath),
            repoMacros);

        Assert.True(show);
        Assert.Equal(1, count);
    }

    // ─── 4. Null / no-solution edge cases ─────────────────────────────────────────────

    [Fact]
    public void NullSolutionPath_ShouldShow_Returns_False_Regardless_Of_Options()
    {
        var options = new MacrosOptions();
        options.TrustSolution(SolutionPath);

        var (show, count) = TrustGateLogic.ShouldShow(
            solutionPath: null,
            isTrusted: false,
            isBlocked: false,
            repoMacros: new[] { Triggered("X") });

        Assert.False(show);
        Assert.Equal(0, count);
    }

    [Fact]
    public void MultiSolution_Trust_Is_Isolated_Per_Path()
    {
        var options = new MacrosOptions();
        var solA = @"C:\repos\A.sln";
        var solB = @"C:\repos\B.sln";
        var repoMacros = new[] { Triggered("OnBuild") };

        options.TrustSolution(solA);

        // A is trusted.
        Assert.True(options.IsSolutionTrusted(solA));
        // B is not trusted.
        Assert.False(options.IsSolutionTrusted(solB));

        var (showB, _) = TrustGateLogic.ShouldShow(
            solB,
            options.IsSolutionTrusted(solB),
            options.IsSolutionBlocked(solB),
            repoMacros);

        Assert.True(showB);
    }
}
