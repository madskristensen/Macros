using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine.Storage;
using Xunit;

namespace Macros.Tests.Integration;

/// <summary>
/// End-to-end integration tests for <see cref="CompositeMacroStore"/> with real
/// <see cref="GlobalMacroStore"/> and <see cref="RepoMacroStore"/> backed by temp folders.
/// Covers the repo-wins merge policy, event propagation through the composite, and
/// the file-system watcher round-trip via external writes.
/// </summary>
public sealed class CompositeStoreIntegrationTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _globalRoot;
    private readonly string _repoRoot;
    private string? _currentRepoPath;

    public CompositeStoreIntegrationTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "Macros.Tests.CompositeIntegration",
            Guid.NewGuid().ToString("N"));
        _globalRoot = Path.Combine(_tempRoot, "global");
        _repoRoot = Path.Combine(_tempRoot, "repo");
        _currentRepoPath = _repoRoot;
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

    private GlobalMacroStore NewGlobal() => new(_globalRoot);
    private RepoMacroStore NewRepo() => new(() => _currentRepoPath);
    private CompositeMacroStore NewComposite(bool ownsChildren = true)
        => new(NewGlobal(), NewRepo(), ownsChildren);

    // ─── 1. Global + Repo round-trip: repo-wins policy ────────────────────────────────

    [Fact]
    public async Task RepoWins_EndToEnd_ListAllAsync_Returns_Repo_Version()
    {
        using var composite = NewComposite();

        await composite.SaveAsAsync("SharedMacro", "// global-version\n", MacroScope.Global);
        await composite.SaveAsAsync("SharedMacro", "// repo-version\n", MacroScope.Repo);

        var all = await composite.ListAllAsync();

        var entry = Assert.Single(all);
        Assert.Equal("SharedMacro", entry.Name);
        Assert.Equal(MacroScope.Repo, entry.Scope);

        var source = await composite.LoadByNameAsync(entry.Name, entry.Scope);
        Assert.Equal("// repo-version\n", source);
    }

    [Fact]
    public async Task RepoWins_MultipleNames_GlobalAndRepo_Merge_Correctly()
    {
        using var composite = NewComposite();

        await composite.SaveAsAsync("Alpha", "// alpha-global\n", MacroScope.Global);
        await composite.SaveAsAsync("Beta", "// beta-global\n", MacroScope.Global);
        await composite.SaveAsAsync("Beta", "// beta-repo\n", MacroScope.Repo);
        await composite.SaveAsAsync("Gamma", "// gamma-repo\n", MacroScope.Repo);

        var all = await composite.ListAllAsync();

        Assert.Equal(3, all.Count);
        Assert.Equal(MacroScope.Global, all.Single(e => e.Name == "Alpha").Scope);
        Assert.Equal(MacroScope.Repo, all.Single(e => e.Name == "Beta").Scope);
        Assert.Equal(MacroScope.Repo, all.Single(e => e.Name == "Gamma").Scope);
    }

    // ─── 2. Move semantics ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task MoveSemantics_WriteToGlobal_ThenRepo_DeleteGlobal_OnlyRepoRemains()
    {
        using var composite = NewComposite();

        // Write to global first.
        await composite.SaveAsAsync("Moveable", "// global\n", MacroScope.Global);

        // Shadow with repo version (overwrite=true so the name can be reused in repo).
        await composite.SaveAsAsync("Moveable", "// repo\n", MacroScope.Repo);

        // Delete from global to "complete the move".
        var deleted = await composite.DeleteAsync("Moveable", MacroScope.Global);
        Assert.True(deleted);

        var all = await composite.ListAllAsync();
        var entry = Assert.Single(all);
        Assert.Equal("Moveable", entry.Name);
        Assert.Equal(MacroScope.Repo, entry.Scope);

        var source = await composite.LoadByNameAsync("Moveable", MacroScope.Repo);
        Assert.Equal("// repo\n", source);
    }

    [Fact]
    public async Task MoveSemantics_Overwrite_Global_With_Repo_SeparateFiles_On_Disk()
    {
        using var global = NewGlobal();
        using var repo = NewRepo();
        using var composite = new CompositeMacroStore(global, repo, ownsChildren: false);

        await composite.SaveAsAsync("Doc", "// global\n", MacroScope.Global);
        await composite.SaveAsAsync("Doc", "// repo\n", MacroScope.Repo);

        // Both files exist on disk independently.
        var globalPath = composite.GetMacroPath("Doc", MacroScope.Global);
        var repoPath = composite.GetMacroPath("Doc", MacroScope.Repo);

        Assert.True(File.Exists(globalPath));
        Assert.True(File.Exists(repoPath));
        Assert.NotEqual(globalPath, repoPath, StringComparer.OrdinalIgnoreCase);

        // ListAllAsync still returns only repo version.
        var all = await composite.ListAllAsync();
        Assert.Single(all);
        Assert.Equal(MacroScope.Repo, all[0].Scope);
    }

    // ─── 3. LibraryChanged event propagation ──────────────────────────────────────────

    [Fact]
    public async Task LibraryChanged_WriteToGlobal_CompositeRaisesAdded_OneEvent()
    {
        using var composite = NewComposite();
        var captured = new List<MacroLibraryChangedEventArgs>();
        composite.LibraryChanged += (_, e) => captured.Add(e);

        await composite.SaveAsAsync("Hello", "// hello\n", MacroScope.Global);

        Assert.Single(captured);
        Assert.Equal(MacroLibraryChangeKind.Added, captured[0].Kind);
        Assert.Equal(MacroScope.Global, captured[0].Scope);
        Assert.Equal("Hello", captured[0].Name);
    }

    [Fact]
    public async Task LibraryChanged_WriteToRepo_CompositeRaisesAdded_OneEvent()
    {
        using var composite = NewComposite();
        var captured = new List<MacroLibraryChangedEventArgs>();
        composite.LibraryChanged += (_, e) => captured.Add(e);

        await composite.SaveAsAsync("RepoMacro", "// repo\n", MacroScope.Repo);

        Assert.Single(captured);
        Assert.Equal(MacroLibraryChangeKind.Added, captured[0].Kind);
        Assert.Equal(MacroScope.Repo, captured[0].Scope);
        Assert.Equal("RepoMacro", captured[0].Name);
    }

    [Fact]
    public async Task LibraryChanged_Delete_CompositeRaisesRemoved()
    {
        using var composite = NewComposite();
        await composite.SaveAsAsync("Erasable", "// data\n", MacroScope.Global);

        var captured = new List<MacroLibraryChangedEventArgs>();
        composite.LibraryChanged += (_, e) => captured.Add(e);

        await composite.DeleteAsync("Erasable", MacroScope.Global);

        Assert.Single(captured);
        Assert.Equal(MacroLibraryChangeKind.Removed, captured[0].Kind);
        Assert.Equal("Erasable", captured[0].Name);
    }

    [Fact]
    public async Task LibraryChanged_Subscriber_Sees_BothScopes_Independently()
    {
        using var composite = NewComposite();
        var captured = new List<MacroLibraryChangedEventArgs>();
        composite.LibraryChanged += (_, e) => captured.Add(e);

        await composite.SaveAsAsync("GEvent", "// g\n", MacroScope.Global);
        await composite.SaveAsAsync("REvent", "// r\n", MacroScope.Repo);

        Assert.Equal(2, captured.Count);
        Assert.Contains(captured, e => e.Scope == MacroScope.Global && e.Name == "GEvent");
        Assert.Contains(captured, e => e.Scope == MacroScope.Repo && e.Name == "REvent");
    }

    // ─── 4. File-system watcher round-trip ────────────────────────────────────────────

    [Fact]
    public async Task ExternalWrite_GlobalFolder_RaisesLibraryChanged_AfterDebounce()
    {
        // Pre-create the global named folder so the watcher can start on subscription.
        var namedFolder = Path.Combine(_globalRoot, "Macros");
        Directory.CreateDirectory(namedFolder);

        using var composite = NewComposite();
        var captured = new List<MacroLibraryChangedEventArgs>();
        composite.LibraryChanged += (_, e) => { lock (captured) captured.Add(e); };

        // Drop a .csx file externally (bypassing the store API) to trigger the watcher.
        var externalFile = Path.Combine(namedFolder, "ExternallyAdded.csx");
        File.WriteAllText(externalFile, "// external");

        // Wait for the 200 ms debounce + buffer.
        await Task.Delay(400);

        lock (captured)
        {
            Assert.True(captured.Count > 0,
                "Expected at least one LibraryChanged event after external file write.");
            Assert.Contains(captured, e =>
                e.Kind == MacroLibraryChangeKind.Added &&
                e.Name == "ExternallyAdded" &&
                e.Scope == MacroScope.Global);
        }
    }

    [Fact]
    public async Task ExternalWrite_RepoFolder_RaisesLibraryChanged_AfterDebounce()
    {
        // Pre-create repo folder so the watcher can start on subscription.
        Directory.CreateDirectory(_repoRoot);

        using var composite = NewComposite();
        var captured = new List<MacroLibraryChangedEventArgs>();
        composite.LibraryChanged += (_, e) => { lock (captured) captured.Add(e); };

        // Trigger the watcher by subscribing through a SaveAsAsync first
        // (which also starts the watcher after creating the directory).
        await composite.SaveAsAsync("Seed", "// seed\n", MacroScope.Repo);
        captured.Clear();

        // Now drop a file externally.
        var externalFile = Path.Combine(_repoRoot, "RepoExternal.csx");
        File.WriteAllText(externalFile, "// external-repo");

        await Task.Delay(400);

        lock (captured)
        {
            Assert.True(captured.Count > 0,
                "Expected at least one LibraryChanged event after external repo file write.");
            Assert.Contains(captured, e =>
                e.Kind == MacroLibraryChangeKind.Added &&
                e.Name == "RepoExternal" &&
                e.Scope == MacroScope.Repo);
        }
    }
}
