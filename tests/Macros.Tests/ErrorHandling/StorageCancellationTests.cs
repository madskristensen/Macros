using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine.Storage;
using Xunit;

namespace Macros.Tests.ErrorHandling;

/// <summary>
/// Pins the v1.0 contract that every async path on <see cref="FileSystemMacroStore"/>
/// honours an already-cancelled <see cref="CancellationToken"/> by throwing
/// <see cref="OperationCanceledException"/> rather than silently completing or
/// returning a partial result. The audit's #7 (Cancellation) category was already
/// implemented across the codebase; this regression suite anchors the contract.
/// </summary>
public sealed class StorageCancellationTests : IDisposable
{
    private readonly string _root;

    public StorageCancellationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "Macros.Tests.Cancel", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private static CancellationToken Cancelled()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        return cts.Token;
    }

    [Fact]
    public async Task SaveCurrentAsync_AlreadyCancelled_Throws()
    {
        var storage = new FileSystemMacroStore(_root);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => storage.SaveCurrentAsync("// content", Cancelled()));
    }

    [Fact]
    public async Task LoadCurrentAsync_AlreadyCancelled_Throws()
    {
        var storage = new FileSystemMacroStore(_root);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => storage.LoadCurrentAsync(Cancelled()));
    }

    [Fact]
    public async Task SaveAsAsync_AlreadyCancelled_Throws()
    {
        var storage = new FileSystemMacroStore(_root);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => storage.SaveAsAsync("M", "// content", MacroScope.Global, overwrite: false, Cancelled()));
    }

    [Fact]
    public async Task ListAsync_AlreadyCancelled_Throws()
    {
        var storage = new FileSystemMacroStore(_root);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => storage.ListAsync(MacroScope.Global, Cancelled()));
    }

    [Fact]
    public async Task LoadByNameAsync_AlreadyCancelled_Throws()
    {
        var storage = new FileSystemMacroStore(_root);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => storage.LoadByNameAsync("M", MacroScope.Global, Cancelled()));
    }

    [Fact]
    public async Task DeleteAsync_AlreadyCancelled_Throws()
    {
        var storage = new FileSystemMacroStore(_root);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => storage.DeleteAsync("M", MacroScope.Global, Cancelled()));
    }
}
