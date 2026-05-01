using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Macros.Engine.Storage;
using Xunit;

namespace Macros.Tests.ErrorHandling;

/// <summary>
/// Pins the v1.0 contract that <see cref="FileSystemMacroStore.SaveAsAsync"/> never
/// corrupts the destination file when a write fails partway. The temp+swap pattern is
/// the safety net; this test exercises the failure mode by saving over an existing
/// macro with the destination file held open by another reader (mimicking the
/// concurrent-VS-instance / disk-full / antivirus-quarantine class of failures).
/// </summary>
public sealed class SaveAsAtomicityTests : IDisposable
{
    private readonly string _root;

    public SaveAsAtomicityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "Macros.Tests.SaveAsAtomic", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task SaveAsAsync_FailureLeavesNoStrayTempFiles_AndOriginalIntact()
    {
        var storage = new FileSystemMacroStore(_root);
        var named = Path.Combine(_root, "Macros");

        // Seed an existing macro so File.Replace is the swap path (the riskier of the two).
        await storage.SaveAsAsync("M", "// Steps: 1\nawait Task.CompletedTask;", MacroScope.Global);
        var destination = Path.Combine(named, "M.csx");
        var originalBytes = File.ReadAllBytes(destination);

        // Hold the destination open with FileShare.Read so File.Replace fails. SaveAs
        // catches → tries to delete the tmp → rethrows. We assert original is intact and
        // no .tmp file leaked into the folder.
        using (var hold = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAnyAsync<IOException>(() =>
                storage.SaveAsAsync("M", "// Steps: 999\n// new content", MacroScope.Global, overwrite: true));
        }

        // Original content untouched.
        Assert.Equal(originalBytes, File.ReadAllBytes(destination));

        // No stray tmp files.
        var tmpStragglers = Directory.EnumerateFiles(named, "*.tmp").ToList();
        Assert.Empty(tmpStragglers);
    }

    [Fact]
    public async Task SaveAsAsync_DestinationLockedByExternalReader_LeavesPriorContent()
    {
        // Variant covering the brand-new file path: hold the destination open with a
        // read-share that DENIES File.Replace (no FileShare.Write), so the second save
        // fails. Pinning behaviour: caller sees a throw, original bytes are intact, no
        // temp stragglers.
        var storage = new FileSystemMacroStore(_root);
        var named = Path.Combine(_root, "Macros");

        await storage.SaveAsAsync("M2", "// original\n", MacroScope.Global);
        var destination = Path.Combine(named, "M2.csx");
        var originalBytes = File.ReadAllBytes(destination);

        using (var hold = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAnyAsync<IOException>(() =>
                storage.SaveAsAsync("M2", "// rewritten\n", MacroScope.Global, overwrite: true));
        }

        Assert.Equal(originalBytes, File.ReadAllBytes(destination));
        Assert.Empty(Directory.EnumerateFiles(named, "*.tmp").ToList());
    }
}
