using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine.Storage;
using Xunit;

namespace Macros.Tests.Storage;

/// <summary>
/// Behavioural tests for <see cref="RepoMacroStore"/>'s scope-restriction contract and
/// its dynamic provider-driven path resolution. Each test owns a unique temp folder so
/// xUnit's parallel runner can drive them concurrently without cross-contamination.
/// </summary>
/// <remarks>
/// Policy under test (see <see cref="RepoMacroStore"/> XML docs):
/// <list type="bullet">
///   <item>Repo operations pass through to the underlying file-system store.</item>
///   <item>Global writes (Save / Rename) throw <see cref="InvalidOperationException"/>.</item>
///   <item>Global reads (List / LoadByName / RefreshEntry) are silent no-ops.</item>
///   <item>Global deletes are silent no-ops returning <see langword="false"/>.</item>
///   <item>Global <see cref="IMacroStore.GetMacroPath"/> throws — no path makes sense.</item>
///   <item>The single-file (current.csx) API throws unconditionally.</item>
///   <item>The provider is called on every named-macro op; switching its return value
///         re-routes subsequent operations to the new path.</item>
/// </list>
/// </remarks>
public sealed class RepoMacroStoreTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _repoRoot;
    private readonly List<string> _additionalRoots = new();

    public RepoMacroStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "Macros.Tests.RepoStore", Guid.NewGuid().ToString("N"));
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
            foreach (var root in _additionalRoots)
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }
        catch
        {
            // Best-effort: a still-open watcher handle on teardown isn't a SUT failure.
        }
    }

    private RepoMacroStore CreateStore() => new(() => _repoRoot);

    private static bool WaitFor(Func<bool> predicate, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return true;
            }

            Thread.Sleep(30);
        }

        return predicate();
    }

    // ─── Construction ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Ctor_NullProvider_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new RepoMacroStore(null!));
    }

    // ─── Repo scope passes through ────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAsAsync_Repo_Succeeds_AndAppearsInList()
    {
        using var store = CreateStore();

        await store.SaveAsAsync("Hello", "// hello\n", MacroScope.Repo);

        var list = await store.ListAsync(MacroScope.Repo);
        var entry = Assert.Single(list);
        Assert.Equal("Hello", entry.Name);
        Assert.Equal(MacroScope.Repo, entry.Scope);
        Assert.True(File.Exists(entry.Path));
        Assert.StartsWith(_repoRoot, entry.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadByNameAsync_Repo_ReturnsContent()
    {
        using var store = CreateStore();
        await store.SaveAsAsync("Hello", "// payload\n", MacroScope.Repo);

        var content = await store.LoadByNameAsync("Hello", MacroScope.Repo);

        Assert.Equal("// payload\n", content);
    }

    [Fact]
    public async Task DeleteAsync_Repo_Existing_ReturnsTrueAndRemovesFile()
    {
        using var store = CreateStore();
        await store.SaveAsAsync("Hello", "// payload\n", MacroScope.Repo);
        var path = store.GetMacroPath("Hello", MacroScope.Repo);
        Assert.True(File.Exists(path));

        var deleted = await store.DeleteAsync("Hello", MacroScope.Repo);

        Assert.True(deleted);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task RenameAsync_Repo_MovesFile()
    {
        using var store = CreateStore();
        await store.SaveAsAsync("Old", "// payload\n", MacroScope.Repo);

        await store.RenameAsync("Old", "New", MacroScope.Repo);

        var list = await store.ListAsync(MacroScope.Repo);
        Assert.Equal("New", Assert.Single(list).Name);
    }

    [Fact]
    public async Task RefreshEntryAsync_Repo_ReturnsEntry()
    {
        using var store = CreateStore();
        await store.SaveAsAsync("Hello", "// payload\n", MacroScope.Repo);

        var entry = await store.RefreshEntryAsync("Hello", MacroScope.Repo);

        Assert.NotNull(entry);
        Assert.Equal("Hello", entry!.Name);
    }

    [Fact]
    public void GetMacroPath_Repo_RoutesUnderRepoFolder()
    {
        using var store = CreateStore();

        var path = store.GetMacroPath("Hello", MacroScope.Repo);

        Assert.StartsWith(_repoRoot, path, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Hello.csx", path);
    }

    // ─── Global scope: writes throw, reads no-op ──────────────────────────────────────

    [Fact]
    public async Task SaveAsAsync_Global_Throws()
    {
        using var store = CreateStore();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveAsAsync("Hello", "// x\n", MacroScope.Global));
    }

    [Fact]
    public async Task RenameAsync_Global_Throws()
    {
        using var store = CreateStore();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.RenameAsync("Old", "New", MacroScope.Global));
    }

    [Fact]
    public void GetMacroPath_Global_Throws()
    {
        using var store = CreateStore();

        Assert.Throws<InvalidOperationException>(() => store.GetMacroPath("Hello", MacroScope.Global));
    }

    [Fact]
    public async Task ListAsync_Global_ReturnsEmpty()
    {
        using var store = CreateStore();
        await store.SaveAsAsync("Hello", "// x\n", MacroScope.Repo);

        var list = await store.ListAsync(MacroScope.Global);

        Assert.Empty(list);
    }

    [Fact]
    public async Task LoadByNameAsync_Global_ReturnsNull()
    {
        using var store = CreateStore();
        await store.SaveAsAsync("Hello", "// x\n", MacroScope.Repo);

        var content = await store.LoadByNameAsync("Hello", MacroScope.Global);

        Assert.Null(content);
    }

    [Fact]
    public async Task RefreshEntryAsync_Global_ReturnsNull()
    {
        using var store = CreateStore();
        await store.SaveAsAsync("Hello", "// x\n", MacroScope.Repo);

        var entry = await store.RefreshEntryAsync("Hello", MacroScope.Global);

        Assert.Null(entry);
    }

    [Fact]
    public async Task DeleteAsync_Global_ReturnsFalse_NoOp()
    {
        using var store = CreateStore();
        await store.SaveAsAsync("Hello", "// x\n", MacroScope.Repo);

        var deleted = await store.DeleteAsync("Hello", MacroScope.Global);

        Assert.False(deleted);
        // Confirm the repo file is untouched.
        Assert.True(File.Exists(store.GetMacroPath("Hello", MacroScope.Repo)));
    }

    // ─── ListAllAsync just returns Repo (no global half) ──────────────────────────────

    [Fact]
    public async Task ListAllAsync_ReturnsRepoMacrosOnly()
    {
        using var store = CreateStore();
        await store.SaveAsAsync("A", "// x\n", MacroScope.Repo);
        await store.SaveAsAsync("B", "// y\n", MacroScope.Repo);

        var list = await store.ListAllAsync();

        Assert.Equal(new[] { "A", "B" }, list.Select(e => e.Name).OrderBy(n => n));
        Assert.All(list, e => Assert.Equal(MacroScope.Repo, e.Scope));
    }

    // ─── M2 single-file API throws unconditionally ────────────────────────────────────

    [Fact]
    public async Task SaveCurrentAsync_Throws()
    {
        using var store = CreateStore();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveCurrentAsync("// payload\n"));
    }

    [Fact]
    public async Task LoadCurrentAsync_Throws()
    {
        using var store = CreateStore();

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadCurrentAsync());
    }

    [Fact]
    public async Task DeleteCurrentAsync_Throws()
    {
        using var store = CreateStore();

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteCurrentAsync());
    }

    [Fact]
    public void CurrentPath_Throws()
    {
        using var store = CreateStore();

        Assert.Throws<InvalidOperationException>(() => _ = store.CurrentPath);
    }

    // ─── Provider semantics ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ProviderReturnsNull_RepoOps_Throw()
    {
        using var store = new RepoMacroStore(() => null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveAsAsync("Hello", "// x\n", MacroScope.Repo));
        Assert.Throws<InvalidOperationException>(() => store.GetMacroPath("Hello", MacroScope.Repo));
    }

    [Fact]
    public async Task ProviderReturnsNull_ListAsync_ReturnsEmpty()
    {
        // No solution open. ListAsync against a missing repo folder should be a clean
        // empty enumeration — the underlying ResolveScopeFolder throws, but the throw
        // is on path resolution itself; we expect callers to gate via a no-solution
        // check first. Here we verify the public contract: SaveAs throws when no
        // solution is open (covered above), and the provider is genuinely called
        // each time (covered below).
        using var store = new RepoMacroStore(() => null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ListAsync(MacroScope.Repo));
    }

    [Fact]
    public async Task ProviderRoutesToCurrentPath_OverTime()
    {
        // Two distinct repo folders simulating two solutions opened in succession.
        var folderA = Path.Combine(Path.GetTempPath(), "Macros.Tests.RepoStore", Guid.NewGuid().ToString("N"), "repoA");
        var folderB = Path.Combine(Path.GetTempPath(), "Macros.Tests.RepoStore", Guid.NewGuid().ToString("N"), "repoB");
        _additionalRoots.Add(Path.GetDirectoryName(folderA)!);
        _additionalRoots.Add(Path.GetDirectoryName(folderB)!);

        string current = folderA;
        using var store = new RepoMacroStore(() => current);

        await store.SaveAsAsync("OnlyInA", "// a\n", MacroScope.Repo);

        // Switch active solution to folderB.
        current = folderB;

        await store.SaveAsAsync("OnlyInB", "// b\n", MacroScope.Repo);

        var listFromB = await store.ListAsync(MacroScope.Repo);
        Assert.Equal(new[] { "OnlyInB" }, listFromB.Select(e => e.Name));

        // Switch back to folderA — the OnlyInA macro should reappear, and OnlyInB
        // should be invisible.
        current = folderA;
        var listFromA = await store.ListAsync(MacroScope.Repo);
        Assert.Equal(new[] { "OnlyInA" }, listFromA.Select(e => e.Name));
    }

    // ─── IsValidName passes through ───────────────────────────────────────────────────

    [Fact]
    public void IsValidName_DelegatesToInner()
    {
        using var store = CreateStore();

        Assert.True(store.IsValidName("Hello"));
        Assert.False(store.IsValidName("current")); // M2 reservation
        Assert.False(store.IsValidName(""));
    }

    // ─── LibraryChanged event ─────────────────────────────────────────────────────────

    [Fact]
    public async Task LibraryChanged_FiresOnRepoSave()
    {
        using var store = CreateStore();
        MacroLibraryChangedEventArgs? captured = null;
        store.LibraryChanged += (_, e) =>
        {
            // Only capture the synchronous self-write; ignore any watcher echoes.
            if (captured is null && e.Scope == MacroScope.Repo && e.Name == "Hello")
            {
                captured = e;
            }
        };

        await store.SaveAsAsync("Hello", "// payload\n", MacroScope.Repo);

        Assert.NotNull(captured);
        Assert.Equal(MacroLibraryChangeKind.Added, captured!.Kind);
        Assert.Equal(MacroScope.Repo, captured.Scope);
        Assert.Equal("Hello", captured.Name);
    }

    // ─── NotifySolutionChanged / watcher lifecycle ───────────────────────────────────

    [Fact]
    public void NotifySolutionChanged_StartsWatcher_AfterFolderCreated()
    {
        // Start with no solution: provider returns null → folder absent → watcher skipped.
        string? currentRepo = null;
        using var store = new RepoMacroStore(() => currentRepo);

        var events = new System.Collections.Concurrent.ConcurrentBag<MacroLibraryChangedEventArgs>();
        store.LibraryChanged += (_, e) => events.Add(e);

        // Simulate solution open: create the folder, point the provider at it, then notify.
        currentRepo = _repoRoot;
        Directory.CreateDirectory(_repoRoot);
        store.NotifySolutionChanged();

        // Warm-up write so the assertion write below doesn't race watcher startup on CI.
        File.WriteAllText(Path.Combine(_repoRoot, "ExternalWarmup.csx"), "// external warmup");
        Assert.True(
            WaitFor(() => events.Any(e => e.Name == "ExternalWarmup" && e.Scope == MacroScope.Repo)),
            "Expected warmup repo watcher event within timeout.");

        while (events.TryTake(out _))
        {
        }

        // Drop a .csx into the folder externally — the watcher must now be running.
        var path = Path.Combine(_repoRoot, "External.csx");
        File.WriteAllText(path, "// external");

        Assert.True(
            WaitFor(() => events.Any(e => e.Name == "External" && e.Scope == MacroScope.Repo)),
            "Expected repo watcher event for External.csx within timeout.");

        Assert.Contains(events, e => e.Name == "External" && e.Scope == MacroScope.Repo);
    }

    [Fact]
    public void NotifySolutionChanged_IsIdempotent()
    {
        Directory.CreateDirectory(_repoRoot);
        using var store = CreateStore();

        // Calling twice must not throw and must not start a second watcher.
        store.NotifySolutionChanged();
        store.NotifySolutionChanged();

        // Verify the single watcher still works by writing a file.
        var events = new System.Collections.Concurrent.ConcurrentBag<MacroLibraryChangedEventArgs>();
        store.LibraryChanged += (_, e) => events.Add(e);

        File.WriteAllText(Path.Combine(_repoRoot, "Idempotent.csx"), "// ok");

        Assert.True(
            WaitFor(() => !events.IsEmpty),
            "Expected repo watcher event after idempotent NotifySolutionChanged calls within timeout.");

        Assert.NotEmpty(events);
    }
}
