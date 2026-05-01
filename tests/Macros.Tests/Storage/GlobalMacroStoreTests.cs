using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Macros.Engine.Storage;
using Xunit;

namespace Macros.Tests.Storage;

/// <summary>
/// Behavioural tests for <see cref="GlobalMacroStore"/>'s scope-restriction contract.
/// Each test owns a unique temp folder so xUnit's parallel runner can drive them
/// concurrently without cross-contamination.
/// </summary>
/// <remarks>
/// Policy under test (see <see cref="GlobalMacroStore"/> XML docs):
/// <list type="bullet">
///   <item>Global operations pass through to the underlying file-system store.</item>
///   <item>Repo writes (Save / Rename) throw <see cref="InvalidOperationException"/>.</item>
///   <item>Repo reads (List / LoadByName / RefreshEntry) are silent no-ops.</item>
///   <item>Repo deletes are silent no-ops returning <see langword="false"/>.</item>
///   <item>Repo <see cref="IMacroStore.GetMacroPath"/> throws — no path makes sense.</item>
/// </list>
/// </remarks>
public sealed class GlobalMacroStoreTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _globalRoot;

    public GlobalMacroStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "Macros.Tests.GlobalStore", Guid.NewGuid().ToString("N"));
        _globalRoot = Path.Combine(_tempRoot, "global");
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

    private GlobalMacroStore CreateStore() => new(_globalRoot);

    // ─── Construction ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Ctor_NullOrWhitespaceFolder_Throws()
    {
        Assert.Throws<ArgumentException>(() => new GlobalMacroStore(string.Empty));
        Assert.Throws<ArgumentException>(() => new GlobalMacroStore("   "));
    }

    // ─── Global scope passes through ──────────────────────────────────────────────────

    [Fact]
    public async Task SaveAsAsync_Global_Succeeds_AndAppearsInList()
    {
        using var store = CreateStore();

        await store.SaveAsAsync("Hello", "// hello\n", MacroScope.Global);

        var list = await store.ListAsync(MacroScope.Global);
        var entry = Assert.Single(list);
        Assert.Equal("Hello", entry.Name);
        Assert.Equal(MacroScope.Global, entry.Scope);
        Assert.True(File.Exists(entry.Path));
    }

    [Fact]
    public async Task LoadByNameAsync_Global_ReturnsContent()
    {
        using var store = CreateStore();
        await store.SaveAsAsync("Hello", "// payload\n", MacroScope.Global);

        var content = await store.LoadByNameAsync("Hello", MacroScope.Global);

        Assert.Equal("// payload\n", content);
    }

    [Fact]
    public async Task DeleteAsync_Global_Existing_ReturnsTrueAndRemovesFile()
    {
        using var store = CreateStore();
        await store.SaveAsAsync("Hello", "// payload\n", MacroScope.Global);
        var path = store.GetMacroPath("Hello", MacroScope.Global);
        Assert.True(File.Exists(path));

        var deleted = await store.DeleteAsync("Hello", MacroScope.Global);

        Assert.True(deleted);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task RenameAsync_Global_MovesFile()
    {
        using var store = CreateStore();
        await store.SaveAsAsync("Old", "// payload\n", MacroScope.Global);

        await store.RenameAsync("Old", "New", MacroScope.Global);

        var list = await store.ListAsync(MacroScope.Global);
        Assert.Equal("New", Assert.Single(list).Name);
    }

    [Fact]
    public async Task RefreshEntryAsync_Global_ReturnsEntry()
    {
        using var store = CreateStore();
        await store.SaveAsAsync("Hello", "// payload\n", MacroScope.Global);

        var entry = await store.RefreshEntryAsync("Hello", MacroScope.Global);

        Assert.NotNull(entry);
        Assert.Equal("Hello", entry!.Name);
    }

    [Fact]
    public void GetMacroPath_Global_RoutesUnderGlobalFolder()
    {
        using var store = CreateStore();

        var path = store.GetMacroPath("Hello", MacroScope.Global);

        Assert.StartsWith(_globalRoot, path, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Hello.csx", path);
    }

    // ─── Repo scope: writes throw, reads no-op ────────────────────────────────────────

    [Fact]
    public async Task SaveAsAsync_Repo_Throws()
    {
        using var store = CreateStore();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveAsAsync("Hello", "// x\n", MacroScope.Repo));
    }

    [Fact]
    public async Task RenameAsync_Repo_Throws()
    {
        using var store = CreateStore();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.RenameAsync("Old", "New", MacroScope.Repo));
    }

    [Fact]
    public void GetMacroPath_Repo_Throws()
    {
        using var store = CreateStore();

        Assert.Throws<InvalidOperationException>(() => store.GetMacroPath("Hello", MacroScope.Repo));
    }

    [Fact]
    public async Task ListAsync_Repo_ReturnsEmpty()
    {
        using var store = CreateStore();
        await store.SaveAsAsync("Hello", "// x\n", MacroScope.Global);

        var list = await store.ListAsync(MacroScope.Repo);

        Assert.Empty(list);
    }

    [Fact]
    public async Task LoadByNameAsync_Repo_ReturnsNull()
    {
        using var store = CreateStore();
        await store.SaveAsAsync("Hello", "// x\n", MacroScope.Global);

        var content = await store.LoadByNameAsync("Hello", MacroScope.Repo);

        Assert.Null(content);
    }

    [Fact]
    public async Task RefreshEntryAsync_Repo_ReturnsNull()
    {
        using var store = CreateStore();
        await store.SaveAsAsync("Hello", "// x\n", MacroScope.Global);

        var entry = await store.RefreshEntryAsync("Hello", MacroScope.Repo);

        Assert.Null(entry);
    }

    [Fact]
    public async Task DeleteAsync_Repo_ReturnsFalse_NoOp()
    {
        using var store = CreateStore();
        await store.SaveAsAsync("Hello", "// x\n", MacroScope.Global);

        var deleted = await store.DeleteAsync("Hello", MacroScope.Repo);

        Assert.False(deleted);
        // Confirm the global file is untouched.
        Assert.True(File.Exists(store.GetMacroPath("Hello", MacroScope.Global)));
    }

    // ─── ListAllAsync just returns Global ─────────────────────────────────────────────

    [Fact]
    public async Task ListAllAsync_ReturnsGlobalMacrosOnly()
    {
        using var store = CreateStore();
        await store.SaveAsAsync("A", "// x\n", MacroScope.Global);
        await store.SaveAsAsync("B", "// y\n", MacroScope.Global);

        var list = await store.ListAllAsync();

        Assert.Equal(new[] { "A", "B" }, list.Select(e => e.Name).OrderBy(n => n));
        Assert.All(list, e => Assert.Equal(MacroScope.Global, e.Scope));
    }

    // ─── M2 single-file API passes through ────────────────────────────────────────────

    [Fact]
    public async Task SaveCurrentAsync_RoundTrips()
    {
        using var store = CreateStore();

        await store.SaveCurrentAsync("// current payload\n");
        var loaded = await store.LoadCurrentAsync();

        Assert.Equal("// current payload\n", loaded);
        Assert.True(File.Exists(store.CurrentPath));
    }

    [Fact]
    public async Task DeleteCurrentAsync_Removes()
    {
        using var store = CreateStore();
        await store.SaveCurrentAsync("// payload\n");

        var deleted = await store.DeleteCurrentAsync();

        Assert.True(deleted);
        Assert.False(File.Exists(store.CurrentPath));
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
    public async Task LibraryChanged_FiresOnGlobalSave()
    {
        using var store = CreateStore();
        MacroLibraryChangedEventArgs? captured = null;
        store.LibraryChanged += (_, e) =>
        {
            // Only capture the synchronous self-write; ignore any watcher echoes.
            if (captured is null && e.Scope == MacroScope.Global && e.Name == "Hello")
            {
                captured = e;
            }
        };

        await store.SaveAsAsync("Hello", "// payload\n", MacroScope.Global);

        Assert.NotNull(captured);
        Assert.Equal(MacroLibraryChangeKind.Added, captured!.Kind);
        Assert.Equal(MacroScope.Global, captured.Scope);
        Assert.Equal("Hello", captured.Name);
    }
}
