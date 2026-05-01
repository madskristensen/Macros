using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine.Storage;
using Xunit;

namespace Macros.Tests.Storage;

/// <summary>
/// Behavioural tests for <see cref="FileSystemMacroStorage"/>. Each test owns a unique
/// temp folder under the system temp root so xUnit's parallel runner can drive them
/// concurrently without cross-contamination.
/// </summary>
public sealed class FileSystemMacroStorageTests : IDisposable
{
    private readonly string _tempRoot;

    public FileSystemMacroStorageTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "Macros.Tests", Guid.NewGuid().ToString("N"));
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
            // Test cleanup best-effort: a still-open handle on a teardown race is not a
            // failure of the system under test.
        }
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsContent()
    {
        var storage = new FileSystemMacroStorage(_tempRoot);
        const string source = "// recorded\nawait DTE.ExecuteCommandAsync(\"Edit.Copy\");\n";

        await storage.SaveCurrentAsync(source);
        var loaded = await storage.LoadCurrentAsync();

        Assert.Equal(source, loaded);
    }

    [Fact]
    public async Task SaveCurrentAsync_CreatesDirectoryIfMissing()
    {
        var nested = Path.Combine(_tempRoot, "does", "not", "exist", "yet");
        var storage = new FileSystemMacroStorage(nested);

        Assert.False(Directory.Exists(nested));

        await storage.SaveCurrentAsync("// hello");

        Assert.True(Directory.Exists(nested));
        Assert.True(File.Exists(Path.Combine(nested, "current.csx")));
    }

    [Fact]
    public async Task SaveCurrentAsync_LeavesNoTempFileBehind()
    {
        var storage = new FileSystemMacroStorage(_tempRoot);

        await storage.SaveCurrentAsync("// first");
        await storage.SaveCurrentAsync("// second");

        // Atomicity is provided by writing to a unique sibling .tmp file then
        // File.Replace/Move; verify no .tmp siblings remain so a successful save is
        // observably clean on disk.
        var leftovers = Directory.GetFiles(_tempRoot, "*.tmp");
        Assert.Empty(leftovers);
        Assert.True(File.Exists(Path.Combine(_tempRoot, "current.csx")));
    }

    [Fact]
    public async Task SaveCurrentAsync_OverwritesExistingFile_PreservingPath()
    {
        var storage = new FileSystemMacroStorage(_tempRoot);

        await storage.SaveCurrentAsync("// v1");
        await storage.SaveCurrentAsync("// v2");

        var loaded = await storage.LoadCurrentAsync();
        Assert.Equal("// v2", loaded);
    }

    [Fact]
    public async Task SaveCurrentAsync_WritesUtf8WithBom()
    {
        var storage = new FileSystemMacroStorage(_tempRoot);

        await storage.SaveCurrentAsync("// æøå");

        var bytes = File.ReadAllBytes(storage.CurrentPath);
        // UTF-8 BOM is EF BB BF — ensures VS opens the file with the right encoding.
        Assert.True(bytes.Length >= 3);
        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);
    }

    [Fact]
    public async Task LoadCurrentAsync_ReturnsNull_WhenFileMissing()
    {
        var storage = new FileSystemMacroStorage(_tempRoot);

        var result = await storage.LoadCurrentAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task LoadCurrentAsync_ReturnsNull_WhenDirectoryMissing()
    {
        var storage = new FileSystemMacroStorage(Path.Combine(_tempRoot, "never-created"));

        var result = await storage.LoadCurrentAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteCurrentAsync_ReturnsTrue_WhenFileExists()
    {
        var storage = new FileSystemMacroStorage(_tempRoot);
        await storage.SaveCurrentAsync("// throwaway");

        var deleted = await storage.DeleteCurrentAsync();

        Assert.True(deleted);
        Assert.False(File.Exists(storage.CurrentPath));
    }

    [Fact]
    public async Task DeleteCurrentAsync_ReturnsFalse_WhenFileMissing()
    {
        var storage = new FileSystemMacroStorage(_tempRoot);

        var deleted = await storage.DeleteCurrentAsync();

        Assert.False(deleted);
    }

    [Fact]
    public async Task SaveCurrentAsync_CancelledBeforeStart_Throws_AndWritesNothing()
    {
        var storage = new FileSystemMacroStorage(_tempRoot);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => storage.SaveCurrentAsync("// should not be written", cts.Token));

        Assert.False(File.Exists(storage.CurrentPath));
        if (Directory.Exists(_tempRoot))
        {
            Assert.Empty(Directory.GetFiles(_tempRoot, "*.tmp"));
        }
    }

    [Fact]
    public async Task LoadCurrentAsync_CancelledBeforeStart_Throws()
    {
        var storage = new FileSystemMacroStorage(_tempRoot);
        await storage.SaveCurrentAsync("// pre-existing");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => storage.LoadCurrentAsync(cts.Token));
    }

    [Fact]
    public async Task ConcurrentSaves_DoNotCrash_AndProduceOneOfTheWrittenValues()
    {
        var storage = new FileSystemMacroStorage(_tempRoot);

        // Last-write-wins semantics — we don't pin which write wins, only that the file
        // ends up with one of the two complete payloads (no torn bytes, no exception).
        var t1 = storage.SaveCurrentAsync("// alpha-payload");
        var t2 = storage.SaveCurrentAsync("// beta-payload");

        await Task.WhenAll(t1, t2);

        var loaded = await storage.LoadCurrentAsync();
        Assert.Contains(loaded, new[] { "// alpha-payload", "// beta-payload" });
    }

    [Fact]
    public void CurrentPath_IsCombinationOfFolderAndCurrentCsx()
    {
        var storage = new FileSystemMacroStorage(_tempRoot);

        Assert.Equal(Path.Combine(_tempRoot, "current.csx"), storage.CurrentPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsMissingFolder(string? folder)
    {
        Assert.Throws<ArgumentException>(() => new FileSystemMacroStorage(folder!));
    }
}
