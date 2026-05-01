using System;
using Macros.Commands.Context;
using Macros.Engine.Storage;
using Macros.Engine.Triggers;
using Xunit;

namespace Macros.Tests.Commands.Context;

/// <summary>
/// Unit tests for <see cref="MoveLogic.ValidateMove"/>. Exercises all guard branches
/// without requiring a VS host.
/// </summary>
public sealed class MoveCommandLogicTests
{
    private static MacroEntry MakeEntry(string name, MacroScope scope) =>
        new(name, scope, $@"C:\macros\{name}.csx", 0,
            new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), 100,
            Array.Empty<TriggerBinding>());

    // ──────────────────────────────────────────────────────────────────────────────────
    // 1. Cannot move when already in target scope
    // ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ValidateMove_WhenAlreadyInTargetScope_ReturnsError()
    {
        var entry = MakeEntry("MyMacro", MacroScope.Global);

        var (ok, error) = MoveLogic.ValidateMove(entry, MacroScope.Global, solutionOpen: true);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateMove_WhenAlreadyInRepoScope_ReturnsError()
    {
        var entry = MakeEntry("MyMacro", MacroScope.Repo);

        var (ok, error) = MoveLogic.ValidateMove(entry, MacroScope.Repo, solutionOpen: true);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    // ──────────────────────────────────────────────────────────────────────────────────
    // 2. Cannot move to Repo when no solution is open
    // ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ValidateMove_ToRepo_WhenNoSolution_ReturnsError()
    {
        var entry = MakeEntry("MyMacro", MacroScope.Global);

        var (ok, error) = MoveLogic.ValidateMove(entry, MacroScope.Repo, solutionOpen: false);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("no solution", error, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────────────────────
    // 3. Valid moves return ok
    // ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ValidateMove_GlobalToRepo_WhenSolutionOpen_ReturnsOk()
    {
        var entry = MakeEntry("MyMacro", MacroScope.Global);

        var (ok, error) = MoveLogic.ValidateMove(entry, MacroScope.Repo, solutionOpen: true);

        Assert.True(ok);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateMove_RepoToGlobal_NoSolutionRequired_ReturnsOk()
    {
        var entry = MakeEntry("MyMacro", MacroScope.Repo);

        var (ok, error) = MoveLogic.ValidateMove(entry, MacroScope.Global, solutionOpen: false);

        Assert.True(ok);
        Assert.Null(error);
    }
}
