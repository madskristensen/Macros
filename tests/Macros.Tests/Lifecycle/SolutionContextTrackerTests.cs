using System;
using System.IO;
using Macros.Lifecycle;
using Xunit;

namespace Macros.Tests.Lifecycle;

/// <summary>
/// Contract-only tests for <see cref="SolutionContextTracker"/>. The full open / close
/// flow exercises <c>VS.Events.SolutionEvents</c>, which requires a hosted VS shell —
/// those scenarios are covered by the manual VSIX integration test plan
/// (<c>m4-exit-criteria</c> SC-T2 / repo lifecycle). Here we verify the public surface
/// that is reachable without standing up the shell:
/// <list type="bullet">
///   <item><see cref="SolutionContextTracker.GetCurrentSolutionDirectory"/> returns
///         <see langword="null"/> until a path has been applied.</item>
///   <item><see cref="SolutionContextTracker.GetCurrentRepoMacrosFolder"/> composes
///         a <c>.vs\Macros</c> path from the tracked directory.</item>
///   <item><see cref="SolutionContextTracker.SolutionChanged"/> fires on each distinct
///         transition and is suppressed for no-op updates (same path twice).</item>
///   <item><see cref="SolutionContextTracker.Dispose"/> is idempotent.</item>
/// </list>
/// </summary>
public sealed class SolutionContextTrackerTests
{
    [Fact]
    public void GetCurrentSolutionDirectory_BeforeAnyApply_ReturnsNull()
    {
        using var tracker = SolutionContextTracker.CreateForTests();

        Assert.Null(tracker.GetCurrentSolutionDirectory());
    }

    [Fact]
    public void GetCurrentRepoMacrosFolder_BeforeAnyApply_ReturnsNull()
    {
        using var tracker = SolutionContextTracker.CreateForTests();

        Assert.Null(tracker.GetCurrentRepoMacrosFolder());
    }

    [Fact]
    public void ApplySolutionPath_NonNull_ExposesDirectoryAndComposesRepoFolder()
    {
        using var tracker = SolutionContextTracker.CreateForTests();
        var dir = Path.Combine(Path.GetTempPath(), "Macros.Tests.Tracker", Guid.NewGuid().ToString("N"));

        tracker.ApplySolutionPath(dir);

        Assert.Equal(dir, tracker.GetCurrentSolutionDirectory());
        var expected = Path.Combine(dir, ".vs", "Macros");
        Assert.Equal(expected, tracker.GetCurrentRepoMacrosFolder());
    }

    [Fact]
    public void ApplySolutionPath_Then_Null_ClearsDirectory()
    {
        using var tracker = SolutionContextTracker.CreateForTests();
        tracker.ApplySolutionPath(@"C:\\temp\\open");
        Assert.NotNull(tracker.GetCurrentSolutionDirectory());

        tracker.ApplySolutionPath(null);

        Assert.Null(tracker.GetCurrentSolutionDirectory());
        Assert.Null(tracker.GetCurrentRepoMacrosFolder());
    }

    [Fact]
    public void SolutionChanged_FiresOnFirstApply()
    {
        using var tracker = SolutionContextTracker.CreateForTests();
        int count = 0;
        tracker.SolutionChanged += (_, _) => count++;

        tracker.ApplySolutionPath(@"C:\\temp\\soln");

        Assert.Equal(1, count);
    }

    [Fact]
    public void SolutionChanged_DoesNotFireForRedundantApply()
    {
        using var tracker = SolutionContextTracker.CreateForTests();
        tracker.ApplySolutionPath(@"C:\\temp\\soln");
        int count = 0;
        tracker.SolutionChanged += (_, _) => count++;

        tracker.ApplySolutionPath(@"C:\\temp\\soln");

        Assert.Equal(0, count);
    }

    [Fact]
    public void SolutionChanged_FiresOnSwitch()
    {
        using var tracker = SolutionContextTracker.CreateForTests();
        tracker.ApplySolutionPath(@"C:\\temp\\solnA");
        int count = 0;
        tracker.SolutionChanged += (_, _) => count++;

        tracker.ApplySolutionPath(@"C:\\temp\\solnB");

        Assert.Equal(1, count);
    }

    [Fact]
    public void SolutionChanged_FiresOnClose()
    {
        using var tracker = SolutionContextTracker.CreateForTests();
        tracker.ApplySolutionPath(@"C:\\temp\\soln");
        int count = 0;
        tracker.SolutionChanged += (_, _) => count++;

        tracker.ApplySolutionPath(null);

        Assert.Equal(1, count);
    }

    [Fact]
    public void ApplySolutionPath_EmptyString_TreatedAsNull()
    {
        using var tracker = SolutionContextTracker.CreateForTests();
        tracker.ApplySolutionPath(@"C:\\temp\\soln");

        tracker.ApplySolutionPath(string.Empty);

        Assert.Null(tracker.GetCurrentSolutionDirectory());
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var tracker = SolutionContextTracker.CreateForTests();
        tracker.Dispose();
        tracker.Dispose();
    }

    [Fact]
    public void DirectoryComparison_IsCaseInsensitive_OnWindows()
    {
        // Same on-disk path with different casing should NOT count as a change — the
        // Windows file system is case-insensitive in practice and re-firing
        // SolutionChanged for cosmetic case differences would needlessly invalidate
        // downstream caches (trigger registry, tool window grouping).
        using var tracker = SolutionContextTracker.CreateForTests();
        tracker.ApplySolutionPath(@"C:\\Temp\\Soln");
        int count = 0;
        tracker.SolutionChanged += (_, _) => count++;

        tracker.ApplySolutionPath(@"c:\\temp\\soln");

        Assert.Equal(0, count);
    }
}
