using System;
using System.IO;
using Macros.Engine.Scripting;
using Macros.Lifecycle;
using Xunit;

namespace Macros.Tests.Lifecycle;

/// <summary>
/// Unit tests for <see cref="IntelliSenseShimRefresher"/>. All tests are pure C# —
/// no VS shell is required because the refresher delegates to
/// <see cref="IntelliSenseShimWriter"/> (file system) and
/// <see cref="SolutionContextTracker.CreateForTests()"/> (in-process test seam).
/// </summary>
public sealed class IntelliSenseShimRefresherTests : IDisposable
{
    // Scratch root under the system temp dir; each test method creates a unique
    // sub-folder by appending the test's Guid, so tests are fully isolated.
    private readonly string _testRoot;

    public IntelliSenseShimRefresherTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "Macros.Tests.ShimRefresher", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
                Directory.Delete(_testRoot, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private string UniqueFolder(string label)
    {
        var path = Path.Combine(_testRoot, label);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string ExpectedShimPath(string root) =>
        Path.Combine(root, IntelliSenseShimWriter.ShimFolderName, IntelliSenseShimWriter.ShimFileName);

    // ── Test 1 ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RefreshGlobal_WritesShimToGlobalFolder()
    {
        var globalRoot = UniqueFolder("global1");
        using var refresher = new IntelliSenseShimRefresher(
            globalFolderProvider: () => globalRoot,
            repoFolderProvider: () => null);

        refresher.RefreshGlobal();

        Assert.True(File.Exists(ExpectedShimPath(globalRoot)),
            $"Expected shim at {ExpectedShimPath(globalRoot)}");
    }

    // ── Test 2 ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RefreshGlobal_EmptyFolder_IsNoOp()
    {
        using var refresher = new IntelliSenseShimRefresher(
            globalFolderProvider: () => string.Empty,
            repoFolderProvider: () => null);

        // Must not throw.
        refresher.RefreshGlobal();
    }

    [Fact]
    public void RefreshGlobal_NullReturnFromProvider_IsNoOp()
    {
        // Simulate a provider that returns null by returning empty (same guard path).
        using var refresher = new IntelliSenseShimRefresher(
            globalFolderProvider: () => "   ",
            repoFolderProvider: () => null);

        // Must not throw.
        refresher.RefreshGlobal();
    }

    // ── Test 3 ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RefreshRepo_NullRepoFolder_IsNoOp()
    {
        var globalRoot = UniqueFolder("global3");
        using var refresher = new IntelliSenseShimRefresher(
            globalFolderProvider: () => globalRoot,
            repoFolderProvider: () => null);

        // No solution open — provider returns null. Must not throw or write anything.
        refresher.RefreshRepo();

        Assert.False(Directory.Exists(Path.Combine(globalRoot, IntelliSenseShimWriter.ShimFolderName)));
    }

    // ── Test 4 ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RefreshRepo_NonNullRepoFolder_WritesShim()
    {
        var repoRoot = UniqueFolder("repo4");
        using var refresher = new IntelliSenseShimRefresher(
            globalFolderProvider: () => UniqueFolder("global4"),
            repoFolderProvider: () => repoRoot);

        refresher.RefreshRepo();

        Assert.True(File.Exists(ExpectedShimPath(repoRoot)),
            $"Expected repo shim at {ExpectedShimPath(repoRoot)}");
    }

    // ── Test 5 ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AttachToTracker_SolutionChanged_TriggersRefreshRepo()
    {
        var repoRoot = UniqueFolder("repo5");

        using var tracker = SolutionContextTracker.CreateForTests();
        using var refresher = new IntelliSenseShimRefresher(
            globalFolderProvider: () => UniqueFolder("global5"),
            repoFolderProvider: () => tracker.GetCurrentRepoMacrosFolder());

        refresher.AttachToTracker(tracker);

        // Simulate solution opening — SolutionChanged fires, RefreshRepo is called.
        tracker.ApplySolutionPath(repoRoot);

        // The repo folder is <repoRoot>\.vs\Macros; confirm the shim exists there.
        var expectedRepoMacrosFolder = Path.Combine(repoRoot, ".vs", "Macros");
        Assert.True(File.Exists(ExpectedShimPath(expectedRepoMacrosFolder)),
            $"Expected shim at {ExpectedShimPath(expectedRepoMacrosFolder)}");
    }

    // ── Test 5b ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void OnSolutionChanged_RefreshesBothGlobalAndRepoShims()
    {
        var globalRoot = UniqueFolder("global5b");
        var repoRoot   = UniqueFolder("repo5b");

        using var tracker = SolutionContextTracker.CreateForTests();
        using var refresher = new IntelliSenseShimRefresher(
            globalFolderProvider: () => globalRoot,
            repoFolderProvider: () => tracker.GetCurrentRepoMacrosFolder());

        refresher.AttachToTracker(tracker);

        // Fires SolutionChanged → OnSolutionChanged → RefreshGlobal() + RefreshRepo().
        tracker.ApplySolutionPath(repoRoot);

        var expectedRepoMacrosFolder = Path.Combine(repoRoot, ".vs", "Macros");
        Assert.True(File.Exists(ExpectedShimPath(globalRoot)),
            $"Expected global shim at {ExpectedShimPath(globalRoot)}");
        Assert.True(File.Exists(ExpectedShimPath(expectedRepoMacrosFolder)),
            $"Expected repo shim at {ExpectedShimPath(expectedRepoMacrosFolder)}");
    }

    // ── Test 5c ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void OnSolutionChanged_RecreatesDeletedGlobalShim()
    {
        var globalRoot = UniqueFolder("global5c");
        var repoRoot   = UniqueFolder("repo5c");

        using var tracker = SolutionContextTracker.CreateForTests();
        using var refresher = new IntelliSenseShimRefresher(
            globalFolderProvider: () => globalRoot,
            repoFolderProvider: () => tracker.GetCurrentRepoMacrosFolder());

        // Seed the global shim.
        refresher.RefreshGlobal();
        var globalShimPath = ExpectedShimPath(globalRoot);
        Assert.True(File.Exists(globalShimPath), "Pre-condition: global shim should exist after seed.");

        // Simulate user deleting the global shim mid-session.
        File.Delete(globalShimPath);
        Assert.False(File.Exists(globalShimPath), "Pre-condition: global shim should be gone after delete.");

        refresher.AttachToTracker(tracker);

        // Fires SolutionChanged → OnSolutionChanged → RefreshGlobal() recreates the shim.
        tracker.ApplySolutionPath(repoRoot);

        Assert.True(File.Exists(globalShimPath),
            $"Expected global shim to be recreated at {globalShimPath}");
    }

    // ── Test 6 ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AttachToTracker_CalledTwice_ThrowsInvalidOperationException()
    {
        using var tracker = SolutionContextTracker.CreateForTests();
        using var refresher = new IntelliSenseShimRefresher(
            globalFolderProvider: () => UniqueFolder("global6"),
            repoFolderProvider: () => null);

        refresher.AttachToTracker(tracker);

        Assert.Throws<InvalidOperationException>(() => refresher.AttachToTracker(tracker));
    }

    // ── Test 7 ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_UnsubscribesFromTracker_NoFurtherWritesAfterDispose()
    {
        var repoRoot = UniqueFolder("repo7");
        var writeCount = 0;

        using var tracker = SolutionContextTracker.CreateForTests();
        var refresher = new IntelliSenseShimRefresher(
            globalFolderProvider: () => UniqueFolder("global7"),
            repoFolderProvider: () =>
            {
                var dir = tracker.GetCurrentRepoMacrosFolder();
                if (dir is not null) writeCount++;
                return dir;
            });

        refresher.AttachToTracker(tracker);

        // Trigger once before dispose — should write.
        tracker.ApplySolutionPath(repoRoot);
        var countBeforeDispose = writeCount;

        refresher.Dispose();

        // Trigger again after dispose — SolutionChanged handler must be unwired.
        tracker.ApplySolutionPath(Path.Combine(_testRoot, "other"));

        Assert.Equal(countBeforeDispose, writeCount);
    }

    // ── Test 8 ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RefreshGlobal_WriterThrows_SurfacesViaOnErrorCallback_NotException()
    {
        // Create a FILE (not a folder) at the path where the shim folder would be created.
        // Directory.CreateDirectory will throw when trying to create it as a directory.
        var globalRoot = UniqueFolder("global8");
        var shimFolderPath = Path.Combine(globalRoot, IntelliSenseShimWriter.ShimFolderName);
        // Create a FILE at the shim-folder path so Directory.CreateDirectory throws.
        File.WriteAllText(shimFolderPath, "blocker");

        string? errorKey = null;
        Exception? capturedException = null;

        using var refresher = new IntelliSenseShimRefresher(
            globalFolderProvider: () => globalRoot,
            repoFolderProvider: () => null,
            onError: (which, ex) =>
            {
                errorKey = which;
                capturedException = ex;
            });

        // Must not throw — the error is routed to the callback.
        refresher.RefreshGlobal();

        Assert.Equal("global", errorKey);
        Assert.NotNull(capturedException);
    }

    // ── Test 9 ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RefreshGlobal_WithPreExistingCsxFile_WritesShimAndRunsMigration()
    {
        var globalRoot = UniqueFolder("global9");

        // Simulate a macro that was recorded before the #load directive feature shipped.
        string fixturePath = Path.Combine(globalRoot, "fixture.csx");
        File.WriteAllText(fixturePath, "// macro\nDTE.ExecuteCommand(\"File.Save\");\n");

        using var refresher = new IntelliSenseShimRefresher(
            globalFolderProvider: () => globalRoot,
            repoFolderProvider: () => null);

        // Must not throw even when the folder contains pre-existing .csx files.
        refresher.RefreshGlobal();

        // Shim must be written.
        Assert.True(File.Exists(ExpectedShimPath(globalRoot)),
            $"Expected shim at {ExpectedShimPath(globalRoot)}");

        // Fixture file must still exist — migration must not corrupt or delete it.
        Assert.True(File.Exists(fixturePath));
    }

    // ── Test 10 ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void RefreshGlobal_WritesShimInBothRootAndNamedSubfolder()
    {
        var globalRoot = UniqueFolder("global10");
        using var refresher = new IntelliSenseShimRefresher(
            globalFolderProvider: () => globalRoot,
            repoFolderProvider: () => null);

        refresher.RefreshGlobal();

        var rootShim = ExpectedShimPath(globalRoot);
        var namedShim = ExpectedShimPath(Path.Combine(globalRoot, "Macros"));

        Assert.True(File.Exists(rootShim),
            $"Expected root shim at {rootShim}");
        Assert.True(File.Exists(namedShim),
            $"Expected named-subfolder shim at {namedShim}");
    }

    // ── Test 11 ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void RefreshGlobal_NamedSubfolderShimMatchesRootShim()
    {
        var globalRoot = UniqueFolder("global11");
        using var refresher = new IntelliSenseShimRefresher(
            globalFolderProvider: () => globalRoot,
            repoFolderProvider: () => null);

        refresher.RefreshGlobal();

        var rootBytes = File.ReadAllBytes(ExpectedShimPath(globalRoot));
        var namedBytes = File.ReadAllBytes(ExpectedShimPath(Path.Combine(globalRoot, "Macros")));

        Assert.Equal(rootBytes, namedBytes);
    }

    // ── Test 12 ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void RefreshGlobal_IsIdempotent_BothLocations()
    {
        var globalRoot = UniqueFolder("global12");
        using var refresher = new IntelliSenseShimRefresher(
            globalFolderProvider: () => globalRoot,
            repoFolderProvider: () => null);

        // Call twice — must not throw and both shims must exist after each call.
        refresher.RefreshGlobal();
        refresher.RefreshGlobal();

        Assert.True(File.Exists(ExpectedShimPath(globalRoot)));
        Assert.True(File.Exists(ExpectedShimPath(Path.Combine(globalRoot, "Macros"))));
    }
}
