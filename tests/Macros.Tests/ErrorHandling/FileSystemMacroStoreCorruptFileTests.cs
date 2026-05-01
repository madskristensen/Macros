using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Macros.Engine.Storage;
using Macros.Engine.Triggers;
using Xunit;

namespace Macros.Tests.ErrorHandling;

/// <summary>
/// Pins the v1.0 contract that <see cref="FileSystemMacroStore"/>'s header parser
/// degrades gracefully on corrupt files. The motivating scenario: a user (or a previous
/// crash) drops binary garbage / random bytes into the named-macro folder. Without the
/// broadened catch in <c>ParseHeader</c>, an exotic decoder failure could propagate up
/// and crash the entire tool window enumeration.
/// </summary>
public sealed class FileSystemMacroStoreCorruptFileTests : IDisposable
{
    private readonly string _root;

    public FileSystemMacroStoreCorruptFileTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "Macros.Tests.Corrupt", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "Macros"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task ListAsync_BinaryGarbageFile_ReturnsEntryWithDefaultMetadata()
    {
        // Plant a "macro" that's actually 4 KB of random binary bytes — no header,
        // invalid as C# script. The listing must still surface the file (so the user
        // can rename / delete it from the tool window) but with step count 0.
        var named = Path.Combine(_root, "Macros");
        var path = Path.Combine(named, "broken.csx");
        var bytes = new byte[4096];
        new Random(42).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);

        var storage = new FileSystemMacroStore(_root);

        var list = await storage.ListAsync(MacroScope.Global);

        var entry = Assert.Single(list);
        Assert.Equal("broken", entry.Name);
        Assert.Equal(0, entry.StepCount);
        var trigger = Assert.Single(entry.Triggers);
        Assert.Equal(TriggerKind.Manual, trigger.Kind);
    }

    [Fact]
    public async Task ListAsync_EmptyFile_ReturnsEntry_NoCrash()
    {
        var named = Path.Combine(_root, "Macros");
        File.WriteAllBytes(Path.Combine(named, "empty.csx"), Array.Empty<byte>());

        var storage = new FileSystemMacroStore(_root);

        var list = await storage.ListAsync(MacroScope.Global);

        Assert.Single(list);
        Assert.Equal("empty", list[0].Name);
        Assert.Equal(0, list[0].StepCount);
    }

    [Fact]
    public async Task ListAsync_HeaderWithMalformedStepsLine_ReturnsZero()
    {
        // The "Steps:" parser expects an integer; "Steps: not-a-number" must yield 0,
        // not throw FormatException out of int.TryParse.
        var named = Path.Combine(_root, "Macros");
        File.WriteAllText(
            Path.Combine(named, "broken-header.csx"),
            "// Steps: not-a-number\nawait Task.CompletedTask;\n");

        var storage = new FileSystemMacroStore(_root);

        var list = await storage.ListAsync(MacroScope.Global);
        var entry = Assert.Single(list);
        Assert.Equal("broken-header", entry.Name);
        Assert.Equal(0, entry.StepCount);
    }

    [Fact]
    public async Task ListAsync_MixedGoodAndCorruptFiles_BothSurface()
    {
        // Defense-in-depth: a single corrupt file must not poison the listing for
        // its sibling well-formed macros.
        var named = Path.Combine(_root, "Macros");
        File.WriteAllText(
            Path.Combine(named, "good.csx"),
            "// Steps: 7\nawait Task.CompletedTask;\n");

        var bytes = new byte[256];
        new Random(1).NextBytes(bytes);
        File.WriteAllBytes(Path.Combine(named, "bad.csx"), bytes);

        var storage = new FileSystemMacroStore(_root);

        var list = await storage.ListAsync(MacroScope.Global);
        Assert.Equal(2, list.Count);

        var good = list.Single(e => e.Name == "good");
        var bad = list.Single(e => e.Name == "bad");
        Assert.Equal(7, good.StepCount);
        Assert.Equal(0, bad.StepCount);
    }
}
