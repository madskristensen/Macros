using System;
using System.IO;
using System.Threading.Tasks;
using Macros.Engine.Storage;
using Xunit;

namespace Macros.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="RepoMacroStore"/> repo lifecycle — simulating
/// solution open/close behaviour via the <c>Func&lt;string?&gt;</c> provider, without
/// requiring an actual VS host or <see cref="Macros.Lifecycle.SolutionContextTracker"/>.
/// </summary>
public sealed class RepoLifecycleTests : IDisposable
{
    private readonly string _tempRoot;

    public RepoLifecycleTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "Macros.Tests.RepoLifecycle",
            Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch { /* Best-effort */ }
    }

    private string PathA => Path.Combine(_tempRoot, "solutionA");
    private string PathB => Path.Combine(_tempRoot, "solutionB");

    // ─── 1. Null provider ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task NullProvider_SaveAsAsync_Throws_InvalidOperationException()
    {
        using var store = new RepoMacroStore(() => null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveAsAsync("MyMacro", "// code\n", MacroScope.Repo));
    }

    [Fact]
    public async Task NullProvider_ListAsync_Repo_Throws_InvalidOperationException()
    {
        // When provider returns null, all Repo-scoped operations throw cleanly rather
        // than crashing with NullReferenceException.
        using var store = new RepoMacroStore(() => null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.ListAsync(MacroScope.Repo));
    }

    [Fact]
    public async Task NullProvider_LoadByNameAsync_Throws_InvalidOperationException()
    {
        using var store = new RepoMacroStore(() => null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.LoadByNameAsync("AnyName", MacroScope.Repo));
    }

    [Fact]
    public async Task NullProvider_DeleteAsync_Throws_InvalidOperationException()
    {
        using var store = new RepoMacroStore(() => null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.DeleteAsync("AnyName", MacroScope.Repo));
    }

    [Fact]
    public void NullProvider_GetMacroPath_Throws_InvalidOperationException()
    {
        using var store = new RepoMacroStore(() => null);

        // GetMacroPath calls ResolveScopeFolder which throws when provider returns null.
        Assert.Throws<InvalidOperationException>(() => store.GetMacroPath("AnyName", MacroScope.Repo));
    }

    // ─── 2. Non-existent path → SaveAsAsync creates the directory ─────────────────────

    [Fact]
    public async Task NonExistentPath_SaveAsAsync_Creates_Directory_And_File()
    {
        var nonExistentPath = Path.Combine(_tempRoot, "brandNewSolution", "macros");
        Assert.False(Directory.Exists(nonExistentPath), "Pre-condition: folder must not exist.");

        using var store = new RepoMacroStore(() => nonExistentPath);

        await store.SaveAsAsync("FirstMacro", "// created\n", MacroScope.Repo);

        Assert.True(Directory.Exists(nonExistentPath), "Store should have created the directory.");
        Assert.True(File.Exists(Path.Combine(nonExistentPath, "FirstMacro.csx")),
            "Macro file should exist after SaveAsAsync.");
    }

    [Fact]
    public async Task NonExistentPath_SaveAsAsync_FileContents_Are_Correct()
    {
        var nonExistentPath = Path.Combine(_tempRoot, "newDir", "macros");
        using var store = new RepoMacroStore(() => nonExistentPath);

        await store.SaveAsAsync("ContentCheck", "// my script\n", MacroScope.Repo);

        var loaded = await store.LoadByNameAsync("ContentCheck", MacroScope.Repo);
        Assert.Equal("// my script\n", loaded);
    }

    // ─── 3. Provider switching paths (solution switch simulation) ─────────────────────

    [Fact]
    public async Task ProviderSwitch_Operations_Route_To_Current_Path()
    {
        var currentPath = PathA;
        using var store = new RepoMacroStore(() => currentPath);

        // Write to solution A.
        await store.SaveAsAsync("ForA", "// for A\n", MacroScope.Repo);

        // Switch to solution B.
        currentPath = PathB;

        // Write to solution B.
        await store.SaveAsAsync("ForB", "// for B\n", MacroScope.Repo);

        // Solution A should have ForA only.
        Assert.True(File.Exists(Path.Combine(PathA, "ForA.csx")));
        Assert.False(File.Exists(Path.Combine(PathA, "ForB.csx")));

        // Solution B should have ForB only.
        Assert.True(File.Exists(Path.Combine(PathB, "ForB.csx")));
        Assert.False(File.Exists(Path.Combine(PathB, "ForA.csx")));
    }

    [Fact]
    public async Task ProviderSwitch_ListAsync_Returns_Items_From_Current_Path()
    {
        Directory.CreateDirectory(PathA);
        Directory.CreateDirectory(PathB);

        var currentPath = PathA;
        using var store = new RepoMacroStore(() => currentPath);

        await store.SaveAsAsync("FromA", "// A\n", MacroScope.Repo);

        currentPath = PathB;
        await store.SaveAsAsync("FromB", "// B\n", MacroScope.Repo);

        // List when pointing at B.
        var listB = await store.ListAsync(MacroScope.Repo);
        Assert.Single(listB);
        Assert.Equal("FromB", listB[0].Name);

        // Switch back to A.
        currentPath = PathA;
        var listA = await store.ListAsync(MacroScope.Repo);
        Assert.Single(listA);
        Assert.Equal("FromA", listA[0].Name);
    }

    [Fact]
    public async Task ProviderSwitch_NullToPath_ThenBack_WriteAndListWork()
    {
        var currentPath = (string?)null;
        using var store = new RepoMacroStore(() => currentPath);

        // No solution — SaveAs and List both throw.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveAsAsync("BeforeOpen", "// before\n", MacroScope.Repo));

        // Solution opens.
        currentPath = PathA;
        await store.SaveAsAsync("AfterOpen", "// after\n", MacroScope.Repo);

        var list = await store.ListAsync(MacroScope.Repo);
        Assert.Single(list);
        Assert.Equal("AfterOpen", list[0].Name);
    }

    [Fact]
    public async Task ProviderSwitch_Delete_Targets_CurrentPath()
    {
        var currentPath = PathA;
        using var store = new RepoMacroStore(() => currentPath);

        await store.SaveAsAsync("ToDelete", "// del\n", MacroScope.Repo);
        Assert.True(File.Exists(Path.Combine(PathA, "ToDelete.csx")));

        // Switch to PathB then back — delete must target PathA.
        currentPath = PathB;
        currentPath = PathA;

        var deleted = await store.DeleteAsync("ToDelete", MacroScope.Repo);

        Assert.True(deleted);
        Assert.False(File.Exists(Path.Combine(PathA, "ToDelete.csx")));
    }
}
