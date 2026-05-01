using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Macros.Engine.Storage;
using Xunit;

namespace Macros.Tests.Storage;

/// <summary>
/// Behavioural tests for <see cref="CompositeMacroStore"/>'s scope-routing contract,
/// the repo-wins merge policy used by <see cref="CompositeMacroStore.ListAllAsync"/>,
/// the aggregated <see cref="IMacroStore.LibraryChanged"/> event, and the
/// <c>ownsChildren</c> dispose semantics. Each test owns a unique pair of temp folders
/// so xUnit's parallel runner can drive them concurrently without cross-contamination.
/// </summary>
public sealed class CompositeMacroStoreTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _globalRoot;
    private readonly string _repoRoot;

    public CompositeMacroStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "Macros.Tests.CompositeStore", Guid.NewGuid().ToString("N"));
        _globalRoot = Path.Combine(_tempRoot, "global");
        _repoRoot = Path.Combine(_tempRoot, "repo");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort: a still-open watcher handle on teardown isn't a SUT failure.
        }
    }

    private GlobalMacroStore NewGlobal() => new(_globalRoot);

    private RepoMacroStore NewRepo() => new(() => _repoRoot);

    private CompositeMacroStore NewComposite(bool ownsChildren = true)
        => new(NewGlobal(), NewRepo(), ownsChildren);

    // ─── Construction ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Ctor_NullGlobal_Throws()
    {
        using var repo = NewRepo();

        Assert.Throws<ArgumentNullException>(() => new CompositeMacroStore(null!, repo));
    }

    [Fact]
    public void Ctor_NullRepo_Throws()
    {
        using var global = NewGlobal();

        Assert.Throws<ArgumentNullException>(() => new CompositeMacroStore(global, null!));
    }

    // ─── Single-file (current.csx) API delegates to global only ───────────────────────

    [Fact]
    public async Task SaveCurrentAsync_DelegatesToGlobalOnly()
    {
        var global = NewGlobal();
        var repo = NewRepo();
        using var composite = new CompositeMacroStore(global, repo);

        await composite.SaveCurrentAsync("// recorded\n");

        // Global received it: the file lives in the global folder.
        var loaded = await global.LoadCurrentAsync();
        Assert.Equal("// recorded\n", loaded);
        Assert.True(File.Exists(global.CurrentPath));

        // Repo did not receive it: nothing on disk under the repo root.
        Assert.False(Directory.Exists(_repoRoot) && Directory.GetFiles(_repoRoot).Length > 0);
    }

    [Fact]
    public async Task LoadCurrentAsync_DelegatesToGlobal()
    {
        var global = NewGlobal();
        using var composite = new CompositeMacroStore(global, NewRepo());
        await global.SaveCurrentAsync("// payload\n");

        var loaded = await composite.LoadCurrentAsync();

        Assert.Equal("// payload\n", loaded);
    }

    [Fact]
    public async Task DeleteCurrentAsync_DelegatesToGlobal()
    {
        var global = NewGlobal();
        using var composite = new CompositeMacroStore(global, NewRepo());
        await global.SaveCurrentAsync("// payload\n");

        var deleted = await composite.DeleteCurrentAsync();

        Assert.True(deleted);
        Assert.False(File.Exists(global.CurrentPath));
    }

    [Fact]
    public void CurrentPath_DelegatesToGlobal()
    {
        var global = NewGlobal();
        using var composite = new CompositeMacroStore(global, NewRepo());

        Assert.Equal(global.CurrentPath, composite.CurrentPath);
    }

    [Fact]
    public void IsValidName_DelegatesToGlobal()
    {
        using var composite = NewComposite();

        Assert.True(composite.IsValidName("Hello"));
        Assert.False(composite.IsValidName(""));
    }

    // ─── Named-macro API: scope routing ───────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_Global_ReturnsOnlyGlobalEntries()
    {
        using var composite = NewComposite();
        await composite.SaveAsAsync("G1", "// g1\n", MacroScope.Global);
        await composite.SaveAsAsync("R1", "// r1\n", MacroScope.Repo);

        var list = await composite.ListAsync(MacroScope.Global);

        var entry = Assert.Single(list);
        Assert.Equal("G1", entry.Name);
        Assert.Equal(MacroScope.Global, entry.Scope);
    }

    [Fact]
    public async Task ListAsync_Repo_ReturnsOnlyRepoEntries()
    {
        using var composite = NewComposite();
        await composite.SaveAsAsync("G1", "// g1\n", MacroScope.Global);
        await composite.SaveAsAsync("R1", "// r1\n", MacroScope.Repo);

        var list = await composite.ListAsync(MacroScope.Repo);

        var entry = Assert.Single(list);
        Assert.Equal("R1", entry.Name);
        Assert.Equal(MacroScope.Repo, entry.Scope);
    }

    [Fact]
    public async Task LoadByNameAsync_RoutesByScope()
    {
        using var composite = NewComposite();
        await composite.SaveAsAsync("Same", "// global-version\n", MacroScope.Global);
        await composite.SaveAsAsync("Same", "// repo-version\n", MacroScope.Repo);

        var fromGlobal = await composite.LoadByNameAsync("Same", MacroScope.Global);
        var fromRepo = await composite.LoadByNameAsync("Same", MacroScope.Repo);

        Assert.Equal("// global-version\n", fromGlobal);
        Assert.Equal("// repo-version\n", fromRepo);
    }

    [Fact]
    public async Task SaveAsAsync_Global_RoutesToGlobalOnly()
    {
        var global = NewGlobal();
        var repo = NewRepo();
        using var composite = new CompositeMacroStore(global, repo);

        await composite.SaveAsAsync("OnlyGlobal", "// g\n", MacroScope.Global);

        Assert.Single(await global.ListAsync(MacroScope.Global));
        Assert.Empty(await repo.ListAsync(MacroScope.Repo));
    }

    [Fact]
    public async Task SaveAsAsync_Repo_RoutesToRepoOnly()
    {
        var global = NewGlobal();
        var repo = NewRepo();
        using var composite = new CompositeMacroStore(global, repo);

        await composite.SaveAsAsync("OnlyRepo", "// r\n", MacroScope.Repo);

        Assert.Empty(await global.ListAsync(MacroScope.Global));
        Assert.Single(await repo.ListAsync(MacroScope.Repo));
    }

    [Fact]
    public async Task DeleteAsync_Repo_RoutesToRepoOnly()
    {
        var global = NewGlobal();
        var repo = NewRepo();
        using var composite = new CompositeMacroStore(global, repo);
        await composite.SaveAsAsync("Same", "// g\n", MacroScope.Global);
        await composite.SaveAsAsync("Same", "// r\n", MacroScope.Repo);

        var deleted = await composite.DeleteAsync("Same", MacroScope.Repo);

        Assert.True(deleted);
        Assert.Single(await global.ListAsync(MacroScope.Global));
        Assert.Empty(await repo.ListAsync(MacroScope.Repo));
    }

    [Fact]
    public async Task RenameAsync_RoutesByScope()
    {
        using var composite = NewComposite();
        await composite.SaveAsAsync("Old", "// payload\n", MacroScope.Repo);

        await composite.RenameAsync("Old", "New", MacroScope.Repo);

        var list = await composite.ListAsync(MacroScope.Repo);
        Assert.Equal("New", Assert.Single(list).Name);
    }

    [Fact]
    public async Task RefreshEntryAsync_RoutesByScope()
    {
        using var composite = NewComposite();
        await composite.SaveAsAsync("Hello", "// payload\n", MacroScope.Global);

        var entry = await composite.RefreshEntryAsync("Hello", MacroScope.Global);

        Assert.NotNull(entry);
        Assert.Equal("Hello", entry!.Name);
        Assert.Equal(MacroScope.Global, entry.Scope);
    }

    [Fact]
    public void GetMacroPath_RoutesByScope()
    {
        using var composite = NewComposite();

        var globalPath = composite.GetMacroPath("Hello", MacroScope.Global);
        var repoPath = composite.GetMacroPath("Hello", MacroScope.Repo);

        Assert.StartsWith(_globalRoot, globalPath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(_repoRoot, repoPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScopeRouting_UnknownScope_Throws()
    {
        using var composite = NewComposite();
        var bogus = (MacroScope)999;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => composite.ListAsync(bogus));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => composite.LoadByNameAsync("X", bogus));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => composite.SaveAsAsync("X", "// x\n", bogus));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => composite.DeleteAsync("X", bogus));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => composite.RenameAsync("X", "Y", bogus));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => composite.RefreshEntryAsync("X", bogus));
        Assert.Throws<ArgumentOutOfRangeException>(() => composite.GetMacroPath("X", bogus));
    }

    // ─── ListAllAsync: repo-wins merge policy ─────────────────────────────────────────

    [Fact]
    public async Task ListAllAsync_MergesGlobalAndRepo_RepoWinsOnCollision()
    {
        using var composite = NewComposite();
        await composite.SaveAsAsync("A", "// a-global\n", MacroScope.Global);
        await composite.SaveAsAsync("B", "// b-global\n", MacroScope.Global);
        await composite.SaveAsAsync("C", "// c-global\n", MacroScope.Global);
        await composite.SaveAsAsync("B", "// b-repo\n", MacroScope.Repo);
        await composite.SaveAsAsync("D", "// d-repo\n", MacroScope.Repo);

        var all = await composite.ListAllAsync();

        Assert.Equal(new[] { "A", "B", "C", "D" }, all.Select(e => e.Name).ToArray());
        // B must come from the repo half (collision resolved repo-wins).
        var b = all.Single(e => e.Name == "B");
        Assert.Equal(MacroScope.Repo, b.Scope);
        Assert.StartsWith(_repoRoot, b.Path, StringComparison.OrdinalIgnoreCase);
        // The non-colliding entries come from their respective halves.
        Assert.Equal(MacroScope.Global, all.Single(e => e.Name == "A").Scope);
        Assert.Equal(MacroScope.Global, all.Single(e => e.Name == "C").Scope);
        Assert.Equal(MacroScope.Repo, all.Single(e => e.Name == "D").Scope);
    }

    [Fact]
    public async Task ListAllAsync_RepoWins_ReturnsRepoSourceOnCollision()
    {
        using var composite = NewComposite();
        await composite.SaveAsAsync("Same", "// global-version\n", MacroScope.Global);
        await composite.SaveAsAsync("Same", "// repo-version\n", MacroScope.Repo);

        var all = await composite.ListAllAsync();

        var entry = Assert.Single(all);
        var loaded = await composite.LoadByNameAsync(entry.Name, entry.Scope);
        Assert.Equal("// repo-version\n", loaded);
    }

    [Fact]
    public async Task ListAllAsync_NoSolution_DegradesToGlobalOnly()
    {
        // Repo provider returns null → RepoMacroStore.ListAsync(Repo) throws InvalidOperationException.
        var global = new GlobalMacroStore(_globalRoot);
        var repo = new RepoMacroStore(() => null);
        using var composite = new CompositeMacroStore(global, repo);
        await composite.SaveAsAsync("OnlyGlobal", "// g\n", MacroScope.Global);

        var all = await composite.ListAllAsync();

        var entry = Assert.Single(all);
        Assert.Equal("OnlyGlobal", entry.Name);
        Assert.Equal(MacroScope.Global, entry.Scope);
    }

    // ─── LibraryChanged aggregation ───────────────────────────────────────────────────

    [Fact]
    public async Task LibraryChanged_RaisesWhenGlobalRaises()
    {
        using var composite = NewComposite();
        var captured = new List<MacroLibraryChangedEventArgs>();
        composite.LibraryChanged += (_, e) => captured.Add(e);

        await composite.SaveAsAsync("Hello", "// payload\n", MacroScope.Global);

        var evt = Assert.Single(captured);
        Assert.Equal(MacroLibraryChangeKind.Added, evt.Kind);
        Assert.Equal(MacroScope.Global, evt.Scope);
        Assert.Equal("Hello", evt.Name);
    }

    [Fact]
    public async Task LibraryChanged_RaisesWhenRepoRaises()
    {
        using var composite = NewComposite();
        var captured = new List<MacroLibraryChangedEventArgs>();
        composite.LibraryChanged += (_, e) => captured.Add(e);

        await composite.SaveAsAsync("Hello", "// payload\n", MacroScope.Repo);

        var evt = Assert.Single(captured);
        Assert.Equal(MacroLibraryChangeKind.Added, evt.Kind);
        Assert.Equal(MacroScope.Repo, evt.Scope);
        Assert.Equal("Hello", evt.Name);
    }

    // ─── Dispose semantics ────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_OwnsChildrenTrue_DisposesBothChildren()
    {
        var global = new TrackingMacroStore();
        var repo = new TrackingMacroStore();
        var composite = new CompositeMacroStore(global, repo, ownsChildren: true);

        composite.Dispose();

        Assert.Equal(1, global.DisposeCount);
        Assert.Equal(1, repo.DisposeCount);
    }

    [Fact]
    public void Dispose_OwnsChildrenFalse_DoesNotDisposeChildren()
    {
        var global = new TrackingMacroStore();
        var repo = new TrackingMacroStore();
        var composite = new CompositeMacroStore(global, repo, ownsChildren: false);

        composite.Dispose();

        Assert.Equal(0, global.DisposeCount);
        Assert.Equal(0, repo.DisposeCount);
    }

    [Fact]
    public void Dispose_UnsubscribesFromChildrenRegardlessOfOwnership()
    {
        var global = new TrackingMacroStore();
        var repo = new TrackingMacroStore();
        var composite = new CompositeMacroStore(global, repo, ownsChildren: false);
        var captured = new List<MacroLibraryChangedEventArgs>();
        composite.LibraryChanged += (_, e) => captured.Add(e);

        composite.Dispose();

        // Subsequent child events must not flow through the disposed composite.
        global.RaiseLibraryChanged(new MacroLibraryChangedEventArgs(MacroLibraryChangeKind.Added, MacroScope.Global, "Post"));
        repo.RaiseLibraryChanged(new MacroLibraryChangedEventArgs(MacroLibraryChangeKind.Added, MacroScope.Repo, "Post"));

        Assert.Empty(captured);
    }

    [Fact]
    public async Task Dispose_OwnsChildrenFalse_LeavesRealChildrenAlive()
    {
        // Sanity check against real children — disposing the composite must not break
        // subsequent calls on the children when ownership wasn't transferred.
        using var global = NewGlobal();
        using var repo = NewRepo();
        var composite = new CompositeMacroStore(global, repo, ownsChildren: false);

        composite.Dispose();

        await global.SaveAsAsync("StillAlive", "// g\n", MacroScope.Global);
        await repo.SaveAsAsync("StillAlive", "// r\n", MacroScope.Repo);
        Assert.Single(await global.ListAsync(MacroScope.Global));
        Assert.Single(await repo.ListAsync(MacroScope.Repo));
    }

    /// <summary>
    /// Minimal in-memory <see cref="IMacroStore"/> used to verify the composite's dispose
    /// and event-aggregation contracts independently of the real file-system stores.
    /// </summary>
    private sealed class TrackingMacroStore : IMacroStore, IDisposable
    {
        public int DisposeCount { get; private set; }

        public string CurrentPath => string.Empty;

        public event EventHandler<MacroLibraryChangedEventArgs>? LibraryChanged;

        public void RaiseLibraryChanged(MacroLibraryChangedEventArgs e)
            => LibraryChanged?.Invoke(this, e);

        public Task SaveCurrentAsync(string source, System.Threading.CancellationToken cancellation = default) => Task.CompletedTask;
        public Task<string?> LoadCurrentAsync(System.Threading.CancellationToken cancellation = default) => Task.FromResult<string?>(null);
        public Task<bool> DeleteCurrentAsync(System.Threading.CancellationToken cancellation = default) => Task.FromResult(false);
        public Task<IReadOnlyList<MacroEntry>> ListAsync(MacroScope scope, System.Threading.CancellationToken cancellation = default)
            => Task.FromResult<IReadOnlyList<MacroEntry>>(Array.Empty<MacroEntry>());
        public Task<IReadOnlyList<MacroEntry>> ListAllAsync(System.Threading.CancellationToken cancellation = default)
            => Task.FromResult<IReadOnlyList<MacroEntry>>(Array.Empty<MacroEntry>());
        public Task<MacroEntry?> RefreshEntryAsync(string name, MacroScope scope, System.Threading.CancellationToken cancellation = default)
            => Task.FromResult<MacroEntry?>(null);
        public Task<string?> LoadByNameAsync(string name, MacroScope scope, System.Threading.CancellationToken cancellation = default)
            => Task.FromResult<string?>(null);
        public Task SaveAsAsync(string name, string source, MacroScope scope, bool overwrite = false, System.Threading.CancellationToken cancellation = default)
            => Task.CompletedTask;
        public Task<bool> DeleteAsync(string name, MacroScope scope, System.Threading.CancellationToken cancellation = default)
            => Task.FromResult(false);
        public Task RenameAsync(string oldName, string newName, MacroScope scope, System.Threading.CancellationToken cancellation = default)
            => Task.CompletedTask;
        public string GetMacroPath(string name, MacroScope scope) => string.Empty;
        public bool IsValidName(string name) => true;

        public void Dispose() => DisposeCount++;
    }
}
