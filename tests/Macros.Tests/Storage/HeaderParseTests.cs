using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Macros.Engine.Storage;
using Xunit;

namespace Macros.Tests.Storage;

/// <summary>
/// Behavioural tests for the new header-parsing helpers on <see cref="FileSystemMacroStore"/>:
/// <see cref="FileSystemMacroStore.ParseStepCount"/>, the implicit header-budget read used
/// by <c>ListAsync</c>/<c>RefreshEntryAsync</c>, and the missing-file path of
/// <see cref="FileSystemMacroStore.RefreshEntryAsync"/>.
/// </summary>
public sealed class HeaderParseTests : IDisposable
{
    private readonly string _root;

    public HeaderParseTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "Macros.Tests.Header", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch { /* best-effort cleanup */ }
    }

    [Theory]
    [InlineData("// Steps: 7\nawait Foo();\n", 7)]
    [InlineData("// steps: 12\n",              12)]
    [InlineData("//   Steps:   3   \n",         3)]
    [InlineData("",                              0)]
    [InlineData("await Foo();\n",                0)]   // no header at all
    [InlineData("// other comment\n",            0)]   // header but no steps
    [InlineData("// Steps: not-a-number\n",      0)]   // unparseable value
    public void ParseStepCount_HandlesVariousHeaders(string headerText, int expected)
    {
        var actual = FileSystemMacroStore.ParseStepCount(headerText);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ParseStepCount_StopsAtFirstNonCommentLine()
    {
        // The Steps directive *after* a code line should be ignored (we only scan the
        // contiguous comment header at the top of the file).
        var text = "// header\nawait Foo();\n// Steps: 99\n";
        Assert.Equal(0, FileSystemMacroStore.ParseStepCount(text));
    }

    [Fact]
    public void ReadHeaderText_StripsUtf8Bom()
    {
        var path = Path.Combine(_root, "bom.csx");
        Directory.CreateDirectory(_root);
        // Write file with explicit UTF-8 BOM.
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes("// Steps: 4\n"))
            .ToArray();
        File.WriteAllBytes(path, bytes);

        var header = FileSystemMacroStore.ReadHeaderText(path);

        Assert.False(header.StartsWith("\uFEFF", StringComparison.Ordinal));
        Assert.Equal(4, FileSystemMacroStore.ParseStepCount(header));
    }

    [Fact]
    public async Task ListAsync_PopulatesStepCount_FromHeader()
    {
        var store = new FileSystemMacroStore(_root);
        await store.SaveAsAsync("MyMacro", "// Steps: 42\nawait Bar();\n", MacroScope.Global, overwrite: true);

        var list = await store.ListAsync(MacroScope.Global);

        var entry = Assert.Single(list);
        Assert.Equal("MyMacro", entry.Name);
        Assert.Equal(42, entry.StepCount);
    }

    [Fact]
    public async Task ListAsync_DefaultsStepCountToZero_WhenNoHeader()
    {
        var store = new FileSystemMacroStore(_root);
        await store.SaveAsAsync("Bare", "await Bar();\n", MacroScope.Global, overwrite: true);

        var list = await store.ListAsync(MacroScope.Global);

        var entry = Assert.Single(list);
        Assert.Equal(0, entry.StepCount);
    }

    [Fact]
    public async Task RefreshEntryAsync_ReturnsEntry_WhenFileExists()
    {
        var store = new FileSystemMacroStore(_root);
        await store.SaveAsAsync("Live", "// Steps: 3\nawait Bar();\n", MacroScope.Global, overwrite: true);

        var entry = await store.RefreshEntryAsync("Live", MacroScope.Global);

        Assert.NotNull(entry);
        Assert.Equal("Live", entry!.Name);
        Assert.Equal(3, entry.StepCount);
    }

    [Fact]
    public async Task RefreshEntryAsync_ReturnsNull_WhenFileMissing()
    {
        var store = new FileSystemMacroStore(_root);

        var entry = await store.RefreshEntryAsync("Ghost", MacroScope.Global);

        Assert.Null(entry);
    }
}
