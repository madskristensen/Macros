using System;
using Macros.Engine.Storage;
using Macros.Engine.Triggers;
using Macros.Options;
using Macros.Trust;
using Xunit;

namespace Macros.Tests.Trust;

/// <summary>
/// Unit tests for <see cref="TrustGate.IsAllowed"/>: the pure decision used by
/// <c>CommandTriggerDispatcher</c> and <c>EventTriggerDispatcher</c> to gate trigger-driven
/// dispatch of repo-scoped macros against per-solution trust state.
/// </summary>
public sealed class TrustGateTests
{
    private const string SolutionPath = @"C:\Repo\App.sln";

    private static MacroEntry Entry(MacroScope scope, string name = "M") => new(
        Name: name,
        Scope: scope,
        Path: $@"X:\fake\{name}.csx",
        StepCount: 0,
        Modified: DateTimeOffset.UtcNow,
        SizeBytes: 0,
        Triggers: new[] { new TriggerBinding(TriggerKind.BeforeCommand, "File.Save") });

    [Fact]
    public void Global_Macro_Is_Always_Allowed_Even_With_No_Solution()
    {
        var opts = new MacrosOptions();
        Assert.True(TrustGate.IsAllowed(Entry(MacroScope.Global), opts, currentSolutionPath: null));
    }

    [Fact]
    public void Global_Macro_Is_Allowed_When_Solution_Is_Blocked()
    {
        // Blocked-solution state must not reach into Global macros — those live in APPDATA
        // and are user-owned, so the per-solution gate doesn't apply.
        var opts = new MacrosOptions();
        opts.BlockSolution(SolutionPath);
        Assert.True(TrustGate.IsAllowed(Entry(MacroScope.Global), opts, SolutionPath));
    }

    [Fact]
    public void Repo_Macro_With_No_Solution_Is_Not_Allowed()
    {
        var opts = new MacrosOptions();
        opts.TrustSolution(SolutionPath); // even with a trust entry on file
        Assert.False(TrustGate.IsAllowed(Entry(MacroScope.Repo), opts, currentSolutionPath: null));
    }

    [Fact]
    public void Repo_Macro_With_Untrusted_Solution_Is_Not_Allowed()
    {
        var opts = new MacrosOptions(); // no trust entries
        Assert.False(TrustGate.IsAllowed(Entry(MacroScope.Repo), opts, SolutionPath));
    }

    [Fact]
    public void Repo_Macro_With_Trusted_Solution_Is_Allowed()
    {
        var opts = new MacrosOptions();
        opts.TrustSolution(SolutionPath);
        Assert.True(TrustGate.IsAllowed(Entry(MacroScope.Repo), opts, SolutionPath));
    }

    [Fact]
    public void Repo_Macro_With_Blocked_Solution_Is_Not_Allowed()
    {
        // BlockSolution removes the trust entry, but pin the contract regardless: even
        // after a TrustSolution call the BlockSolution win must not leak repo dispatch.
        var opts = new MacrosOptions();
        opts.BlockSolution(SolutionPath);
        Assert.False(TrustGate.IsAllowed(Entry(MacroScope.Repo), opts, SolutionPath));
    }

    [Fact]
    public void Repo_Macro_With_Trust_Then_Block_Is_Not_Allowed()
    {
        var opts = new MacrosOptions();
        opts.TrustSolution(SolutionPath);
        opts.BlockSolution(SolutionPath);
        Assert.False(TrustGate.IsAllowed(Entry(MacroScope.Repo), opts, SolutionPath));
    }

    [Fact]
    public void Repo_Macro_With_Trusted_Solution_Path_Case_Insensitive()
    {
        // MacrosOptions.IsSolutionTrusted canonicalises via Path.GetFullPath + ToLowerInvariant.
        // The gate must inherit that behaviour — VS sometimes hands back the .sln in a
        // different case than what the user originally typed when trusting.
        var opts = new MacrosOptions();
        opts.TrustSolution(@"C:\Repo\App.sln");
        Assert.True(TrustGate.IsAllowed(Entry(MacroScope.Repo), opts, @"C:\REPO\APP.SLN"));
    }

    [Fact]
    public void Null_Entry_Returns_False()
    {
        var opts = new MacrosOptions();
        Assert.False(TrustGate.IsAllowed(null!, opts, SolutionPath));
    }

    [Fact]
    public void Null_Options_Returns_False()
    {
        Assert.False(TrustGate.IsAllowed(Entry(MacroScope.Repo), null!, SolutionPath));
    }

    [Fact]
    public void Repo_Macro_With_Empty_Solution_Path_Is_Not_Allowed()
    {
        var opts = new MacrosOptions();
        opts.TrustSolution(SolutionPath);
        Assert.False(TrustGate.IsAllowed(Entry(MacroScope.Repo), opts, currentSolutionPath: ""));
    }
}
