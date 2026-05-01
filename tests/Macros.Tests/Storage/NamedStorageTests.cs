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
/// Behavioural tests for the M3 named-macro API on <see cref="FileSystemMacroStorage"/>.
/// Each test owns a unique temp folder under the system temp root so xUnit's parallel
/// runner can drive them concurrently without cross-contamination.
/// </summary>
public sealed class NamedStorageTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _globalRoot;
    private readonly string _repoRoot;

    public NamedStorageTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "Macros.Tests.NamedStorage", Guid.NewGuid().ToString("N"));
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
            // Best-effort: a still-open handle on teardown isn't a SUT failure.
        }
    }

    private FileSystemMacroStorage CreateStorage(bool withRepo = false)
    {
        return new FileSystemMacroStorage(
            _globalRoot,
            withRepo ? () => _repoRoot : null);
    }

    [Fact]
    public async Task ListAsync_EmptyFolder_ReturnsEmptyList()
    {
        var storage = CreateStorage();

        var list = await storage.ListAsync(MacroScope.Global);

        Assert.Empty(list);
    }

    [Fact]
    public async Task SaveAs_New_CreatesFileAndAppearsInList()
    {
        var storage = CreateStorage();

        await storage.SaveAsAsync("Hello", "// hello world\n", MacroScope.Global);

        var list = await storage.ListAsync(MacroScope.Global);
        var entry = Assert.Single(list);
        Assert.Equal("Hello", entry.Name);
        Assert.Equal(MacroScope.Global, entry.Scope);
        Assert.True(File.Exists(entry.FilePath));
        Assert.Equal("Hello.csx", Path.GetFileName(entry.FilePath));
    }

    [Fact]
    public async Task SaveAs_Existing_WithoutOverwrite_Throws()
    {
        var storage = CreateStorage();
        await storage.SaveAsAsync("Same", "// v1", MacroScope.Global);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.SaveAsAsync("Same", "// v2", MacroScope.Global));
        Assert.Contains("Same", ex.Message);
    }

    [Fact]
    public async Task SaveAs_Existing_WithOverwrite_Succeeds_AndRaisesModified()
    {
        var storage = CreateStorage();
        await storage.SaveAsAsync("Same", "// v1", MacroScope.Global);

        var events = new List<MacroLibraryChangedEventArgs>();
        storage.LibraryChanged += (_, e) => events.Add(e);

        await storage.SaveAsAsync("Same", "// v2", MacroScope.Global, overwrite: true);

        var loaded = await storage.LoadByNameAsync("Same", MacroScope.Global);
        Assert.Equal("// v2", loaded);

        var evt = Assert.Single(events);
        Assert.Equal(MacroLibraryChangeKind.Modified, evt.Kind);
        Assert.Equal("Same", evt.Name);
        Assert.Equal(MacroScope.Global, evt.Scope);
        Assert.Null(evt.OldName);
    }

    [Fact]
    public async Task SaveAs_New_RaisesAdded()
    {
        var storage = CreateStorage();
        var events = new List<MacroLibraryChangedEventArgs>();
        storage.LibraryChanged += (_, e) => events.Add(e);

        await storage.SaveAsAsync("Fresh", "// new", MacroScope.Global);

        var evt = Assert.Single(events);
        Assert.Equal(MacroLibraryChangeKind.Added, evt.Kind);
        Assert.Equal("Fresh", evt.Name);
    }

    [Fact]
    public async Task Delete_Existing_ReturnsTrue_AndRaisesRemoved()
    {
        var storage = CreateStorage();
        await storage.SaveAsAsync("Bye", "// goes away", MacroScope.Global);

        var events = new List<MacroLibraryChangedEventArgs>();
        storage.LibraryChanged += (_, e) => events.Add(e);

        var result = await storage.DeleteAsync("Bye", MacroScope.Global);

        Assert.True(result);
        var evt = Assert.Single(events);
        Assert.Equal(MacroLibraryChangeKind.Removed, evt.Kind);
        Assert.Equal("Bye", evt.Name);
    }

    [Fact]
    public async Task Delete_Missing_ReturnsFalse_NoEvent()
    {
        var storage = CreateStorage();

        var events = new List<MacroLibraryChangedEventArgs>();
        storage.LibraryChanged += (_, e) => events.Add(e);

        var result = await storage.DeleteAsync("Nope", MacroScope.Global);

        Assert.False(result);
        Assert.Empty(events);
    }

    [Fact]
    public async Task Rename_Existing_MovesFile_AndRaisesRenamed()
    {
        var storage = CreateStorage();
        await storage.SaveAsAsync("Old", "// content", MacroScope.Global);

        var events = new List<MacroLibraryChangedEventArgs>();
        storage.LibraryChanged += (_, e) => events.Add(e);

        await storage.RenameAsync("Old", "New", MacroScope.Global);

        Assert.Null(await storage.LoadByNameAsync("Old", MacroScope.Global));
        Assert.Equal("// content", await storage.LoadByNameAsync("New", MacroScope.Global));

        var evt = Assert.Single(events);
        Assert.Equal(MacroLibraryChangeKind.Renamed, evt.Kind);
        Assert.Equal("New", evt.Name);
        Assert.Equal("Old", evt.OldName);
    }

    [Fact]
    public async Task Rename_TargetExists_Throws()
    {
        var storage = CreateStorage();
        await storage.SaveAsAsync("Source", "// a", MacroScope.Global);
        await storage.SaveAsAsync("Target", "// b", MacroScope.Global);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.RenameAsync("Source", "Target", MacroScope.Global));

        // Both files should still exist.
        Assert.NotNull(await storage.LoadByNameAsync("Source", MacroScope.Global));
        Assert.NotNull(await storage.LoadByNameAsync("Target", MacroScope.Global));
    }

    [Fact]
    public async Task Rename_SourceMissing_Throws()
    {
        var storage = CreateStorage();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.RenameAsync("Ghost", "Body", MacroScope.Global));
    }

    [Fact]
    public async Task LoadByName_Missing_ReturnsNull()
    {
        var storage = CreateStorage();

        var loaded = await storage.LoadByNameAsync("Nothing", MacroScope.Global);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task LoadByName_Existing_ReturnsContent()
    {
        var storage = CreateStorage();
        const string source = "// loaded\nawait DTE.ExecuteCommandAsync(\"Edit.Copy\");\n";
        await storage.SaveAsAsync("Round", source, MacroScope.Global);

        var loaded = await storage.LoadByNameAsync("Round", MacroScope.Global);

        Assert.Equal(source, loaded);
    }

    [Fact]
    public async Task RepoScope_NoProvider_Throws()
    {
        var storage = CreateStorage(withRepo: false);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.SaveAsAsync("Foo", "// x", MacroScope.Repo));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.LoadByNameAsync("Foo", MacroScope.Repo));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.DeleteAsync("Foo", MacroScope.Repo));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.ListAsync(MacroScope.Repo));
    }

    [Fact]
    public async Task RepoScope_WithProvider_UsesProvidedPath()
    {
        var storage = CreateStorage(withRepo: true);

        await storage.SaveAsAsync("RepoOnly", "// repo content", MacroScope.Repo);

        var expected = Path.Combine(_repoRoot, "RepoOnly.csx");
        Assert.True(File.Exists(expected));

        // And not present in global scope.
        Assert.Null(await storage.LoadByNameAsync("RepoOnly", MacroScope.Global));
    }

    [Fact]
    public async Task ListAll_MergesGlobalAndRepo_WhenBothProvided()
    {
        var storage = CreateStorage(withRepo: true);
        await storage.SaveAsAsync("InGlobal", "// g", MacroScope.Global);
        await storage.SaveAsAsync("InRepo", "// r", MacroScope.Repo);

        var all = await storage.ListAllAsync();

        var byName = all.ToDictionary(d => d.Name, d => d.Scope);
        Assert.Equal(MacroScope.Global, byName["InGlobal"]);
        Assert.Equal(MacroScope.Repo, byName["InRepo"]);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task ListAll_NoSolution_ReturnsGlobalOnly()
    {
        var storage = CreateStorage(withRepo: false);
        await storage.SaveAsAsync("OnlyOne", "// g", MacroScope.Global);

        var all = await storage.ListAllAsync();

        var entry = Assert.Single(all);
        Assert.Equal("OnlyOne", entry.Name);
        Assert.Equal(MacroScope.Global, entry.Scope);
    }

    [Fact]
    public async Task List_FiltersOutCurrentCsx()
    {
        var storage = CreateStorage();
        await storage.SaveAsAsync("Real", "// x", MacroScope.Global);

        // Plant a stray current.csx in the global named folder and ensure ListAsync hides it.
        var folder = Path.Combine(_globalRoot, "Macros");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "current.csx"), "// reserved");

        var list = await storage.ListAsync(MacroScope.Global);

        var entry = Assert.Single(list);
        Assert.Equal("Real", entry.Name);
    }

    [Fact]
    public async Task List_SortsByNameAscending()
    {
        var storage = CreateStorage();
        await storage.SaveAsAsync("Charlie", "// c", MacroScope.Global);
        await storage.SaveAsAsync("Alpha", "// a", MacroScope.Global);
        await storage.SaveAsAsync("Bravo", "// b", MacroScope.Global);

        var list = await storage.ListAsync(MacroScope.Global);

        Assert.Equal(new[] { "Alpha", "Bravo", "Charlie" }, list.Select(d => d.Name).ToArray());
    }

    [Fact]
    public async Task GetMacroPath_GlobalScope_ReturnsGlobalNamedSubfolder()
    {
        var storage = CreateStorage();

        var path = storage.GetMacroPath("Foo", MacroScope.Global);

        Assert.Equal(Path.Combine(_globalRoot, "Macros", "Foo.csx"), path);
    }

    [Fact]
    public void GetMacroPath_InvalidName_Throws()
    {
        var storage = CreateStorage();

        Assert.Throws<ArgumentException>(() => storage.GetMacroPath("bad/name", MacroScope.Global));
    }

    [Fact]
    public async Task ConcurrentSaveAs_DifferentNames_BothSucceed()
    {
        var storage = CreateStorage();

        var tasks = Enumerable.Range(0, 16)
            .Select(i => storage.SaveAsAsync($"Macro{i:D2}", $"// content {i}", MacroScope.Global))
            .ToArray();

        await Task.WhenAll(tasks);

        var list = await storage.ListAsync(MacroScope.Global);
        Assert.Equal(16, list.Count);
    }

    [Fact]
    public async Task ConcurrentSaveAs_SameName_Serialized_LastWins()
    {
        var storage = CreateStorage();
        // First save creates the file.
        await storage.SaveAsAsync("Race", "// initial", MacroScope.Global);

        // Now fire many concurrent overwrites; they must serialize, all complete, and one
        // of the contents must end up persisted (no partial corruption, no crash).
        var contents = Enumerable.Range(0, 20).Select(i => $"// version {i}\n").ToArray();
        var tasks = contents
            .Select(c => storage.SaveAsAsync("Race", c, MacroScope.Global, overwrite: true))
            .ToArray();

        await Task.WhenAll(tasks);

        var loaded = await storage.LoadByNameAsync("Race", MacroScope.Global);
        Assert.NotNull(loaded);
        Assert.Contains(loaded, contents);

        // No leftover .tmp files from any racing writer.
        var leftovers = Directory.GetFiles(Path.Combine(_globalRoot, "Macros"), "*.tmp");
        Assert.Empty(leftovers);
    }

    [Fact]
    public async Task RepoFolderProvider_DynamicChange_HonoursLatestValue()
    {
        // The provider returns null first, then a path: storage should switch from
        // throwing to writing successfully without being recreated.
        string? current = null;
        var storage = new FileSystemMacroStorage(_globalRoot, () => current);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.SaveAsAsync("X", "// x", MacroScope.Repo));

        current = _repoRoot;
        await storage.SaveAsAsync("X", "// x", MacroScope.Repo);

        var path = Path.Combine(_repoRoot, "X.csx");
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task SaveAs_PreservesCurrentCsxAtGlobalRoot()
    {
        // The named API writes to <global>\Macros\, the M2 single-file API writes to
        // <global>\current.csx. Verify they don't trample each other.
        var storage = CreateStorage();
        await storage.SaveCurrentAsync("// ad-hoc");
        await storage.SaveAsAsync("Library", "// saved", MacroScope.Global);

        Assert.True(File.Exists(Path.Combine(_globalRoot, "current.csx")));
        Assert.True(File.Exists(Path.Combine(_globalRoot, "Macros", "Library.csx")));
    }
}
