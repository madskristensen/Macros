using System;
using System.IO;
using Macros.Engine.Scripting;
using Xunit;

namespace Macros.Tests.Scripting;

/// <summary>
/// Behavioural tests for <see cref="IntelliSenseShimWriter"/>. Each test owns a unique
/// temp folder so xUnit's parallel runner can drive them concurrently without
/// cross-contamination.
/// </summary>
public sealed class IntelliSenseShimWriterTests : IDisposable
{
    private readonly string _tempRoot;

    public IntelliSenseShimWriterTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "Macros.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
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
            // Best-effort: a still-open handle on a teardown race is not a test failure.
        }
    }

    // ── basic write ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Write_CreatesShimFileOnDisk()
    {
        var result = IntelliSenseShimWriter.Write(_tempRoot);
        Assert.True(File.Exists(result.ShimPath));
    }

    [Fact]
    public void Write_ShimPath_IsUnderExpectedFolder()
    {
        var result = IntelliSenseShimWriter.Write(_tempRoot);

        string expected = Path.Combine(
            _tempRoot,
            IntelliSenseShimWriter.ShimFolderName,
            IntelliSenseShimWriter.ShimFileName);

        Assert.Equal(expected, result.ShimPath);
    }

    [Fact]
    public void Write_FirstCall_ReturnsWroteTrue()
    {
        var result = IntelliSenseShimWriter.Write(_tempRoot);
        Assert.True(result.Wrote);
    }

    [Fact]
    public void Write_CreatesIntellisenseFolder_WhenMissing()
    {
        string expectedFolder = Path.Combine(_tempRoot, IntelliSenseShimWriter.ShimFolderName);
        Assert.False(Directory.Exists(expectedFolder));

        IntelliSenseShimWriter.Write(_tempRoot);

        Assert.True(Directory.Exists(expectedFolder));
    }

    [Fact]
    public void Write_CreatesIntellisenseFolder_EvenForNestedStoreRoot()
    {
        string nested = Path.Combine(_tempRoot, "does", "not", "exist", "yet");
        Directory.CreateDirectory(nested);   // store-root exists; sub-folder does not

        var result = IntelliSenseShimWriter.Write(nested);

        Assert.True(File.Exists(result.ShimPath));
    }

    // ── idempotency ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Write_SecondCallUnchanged_ReturnsWroteFalse()
    {
        IntelliSenseShimWriter.Write(_tempRoot);
        var second = IntelliSenseShimWriter.Write(_tempRoot);

        Assert.False(second.Wrote);
    }

    [Fact]
    public void Write_FileExternallyMutated_ReturnsWroteTrue()
    {
        IntelliSenseShimWriter.Write(_tempRoot);

        string shimPath = Path.Combine(
            _tempRoot,
            IntelliSenseShimWriter.ShimFolderName,
            IntelliSenseShimWriter.ShimFileName);

        File.WriteAllText(shimPath, "// stale content");

        var result = IntelliSenseShimWriter.Write(_tempRoot);
        Assert.True(result.Wrote);
    }

    [Fact]
    public void Write_AfterExternalMutation_ContentIsRestoredToValidShim()
    {
        IntelliSenseShimWriter.Write(_tempRoot);

        string shimPath = Path.Combine(
            _tempRoot,
            IntelliSenseShimWriter.ShimFolderName,
            IntelliSenseShimWriter.ShimFileName);

        File.WriteAllText(shimPath, "// stale content");
        IntelliSenseShimWriter.Write(_tempRoot);

        string content = File.ReadAllText(shimPath);
        Assert.Contains("// Macros — IntelliSense Shim", content, StringComparison.Ordinal);
    }

    // ── atomic write ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Write_LeavesNoTmpFileBehind()
    {
        IntelliSenseShimWriter.Write(_tempRoot);

        string shimFolder = Path.Combine(_tempRoot, IntelliSenseShimWriter.ShimFolderName);
        string[] tmpFiles = Directory.GetFiles(shimFolder, "*.tmp");

        Assert.Empty(tmpFiles);
    }

    // ── assembly paths ───────────────────────────────────────────────────────────────

    [Fact]
    public void Write_ReturnsNonEmptyResolvedAssemblyPaths()
    {
        var result = IntelliSenseShimWriter.Write(_tempRoot);
        Assert.NotEmpty(result.ResolvedAssemblyPaths);
    }

    [Fact]
    public void Write_ResolvedAssemblyPaths_AreAbsolutePaths()
    {
        var result = IntelliSenseShimWriter.Write(_tempRoot);

        foreach (string path in result.ResolvedAssemblyPaths)
        {
            Assert.True(
                Path.IsPathRooted(path),
                $"Expected absolute path but got: {path}");
        }
    }

    [Fact]
    public void Write_ShimContent_ContainsRDirectiveForEachResolvedPath()
    {
        var result = IntelliSenseShimWriter.Write(_tempRoot);
        string content = File.ReadAllText(result.ShimPath);

        foreach (string path in result.ResolvedAssemblyPaths)
        {
            // #r "path" — raw string (no verbatim @ prefix; Roslyn #r doesn't accept @"").
            Assert.Contains($"#r \"{path}\"", content, StringComparison.Ordinal);
        }
    }

    // ── argument validation ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Write_NullOrWhitespaceStoreRoot_ThrowsArgumentException(string? storeRoot)
    {
        var ex = Assert.Throws<ArgumentException>(() => IntelliSenseShimWriter.Write(storeRoot!));
        Assert.Equal("storeRoot", ex.ParamName);
    }

    // ── Deduplicate ───────────────────────────────────────────────────────────────────

    [Fact]
    public void ResolveAssemblyPaths_DeduplicatesIdenticalPaths_WhenInteropAssembliesShareDll()
    {
        // Simulate VS18 Preview: EnvDTE and EnvDTE80 both resolve to the same path.
        var shimPath = @"C:\VS\Interop.dll";
        var shellPath = @"C:\VS\Shell.dll";

        var deduplicated = IntelliSenseShimWriter.Deduplicate(
            new[] { shimPath, shimPath, shellPath });

        Assert.Equal(2, deduplicated.Count);
        Assert.Equal(shimPath, deduplicated[0]);
        Assert.Equal(shellPath, deduplicated[1]);
    }

    [Fact]
    public void Deduplicate_PreservesOriginalOrdering()
    {
        var input = new[] { "a.dll", "b.dll", "a.dll", "c.dll" };
        var result = IntelliSenseShimWriter.Deduplicate(input);

        Assert.Equal(new[] { "a.dll", "b.dll", "c.dll" }, result);
    }

    [Fact]
    public void Deduplicate_IsCaseInsensitive()
    {
        var input = new[] { @"C:\Foo.dll", @"c:\foo.dll" };
        var result = IntelliSenseShimWriter.Deduplicate(input);

        Assert.Single(result);
        Assert.Equal(@"C:\Foo.dll", result[0]); // first occurrence wins
    }
}
