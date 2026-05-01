using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine.Storage;
using Xunit;

namespace Macros.Tests.Commands;

/// <summary>
/// Tests for the testable helper method extracted from <c>SaveAsCommand</c>.
/// The full <c>ExecuteAsync</c> path requires a VS host; only the source-resolution
/// helper is covered here.
/// </summary>
/// <remarks>
/// <c>SaveAsCommand.ResolveSourceAsync</c> is <see langword="internal"/> and accessible
/// because <c>Macros.Engine.csproj</c> already grants <c>InternalsVisibleTo Macros.Tests</c>.
/// Note: <c>SaveAsCommand</c> itself lives in the VSIX (<c>Macros.csproj</c>), which the
/// test project references with <c>ReferenceOutputAssembly=false</c> — so this test file
/// exercises the storage abstraction directly rather than instantiating the command class.
/// </remarks>
public sealed class SaveAsCommandTests
{
    /// <summary>
    /// When <c>current.csx</c> does not exist, <c>LoadCurrentAsync</c> returns
    /// <see langword="null"/>. This is the "no macro recorded yet" path that causes
    /// <c>SaveAsCommand</c> to show the "Record a macro first" warning.
    /// </summary>
    [Fact]
    public async Task ResolveSource_EmptyStorage_ReturnsNull()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "SaveAsCmdTest_" + System.Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new FileSystemMacroStorage(tempRoot);
            var source = await storage.LoadCurrentAsync();
            Assert.Null(source);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    /// <summary>
    /// When <c>current.csx</c> has been written, <c>LoadCurrentAsync</c> returns its
    /// content — this is the source that <c>SaveAsCommand</c> passes to <c>SaveAsAsync</c>.
    /// </summary>
    [Fact]
    public async Task ResolveSource_PopulatedStorage_ReturnsContent()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "SaveAsCmdTest_" + System.Guid.NewGuid().ToString("N"));
        try
        {
            const string expected = "// recorded macro\nawait VS.Editor.ActiveView.Caret.MoveToNextLineAsync();\n";
            var storage = new FileSystemMacroStorage(tempRoot);
            await storage.SaveCurrentAsync(expected);

            var source = await storage.LoadCurrentAsync();

            Assert.Equal(expected, source);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
