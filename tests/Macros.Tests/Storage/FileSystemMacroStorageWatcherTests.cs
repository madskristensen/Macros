using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine.Storage;
using Xunit;

namespace Macros.Tests.Storage;

/// <summary>
/// Tests that <see cref="FileSystemMacroStorage"/> translates external filesystem
/// mutations into <see cref="IMacroStorage.LibraryChanged"/> events via its embedded
/// <see cref="FileSystemWatcher"/>.
/// </summary>
public sealed class FileSystemMacroStorageWatcherTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _globalRoot;
    private readonly string _namedFolder;

    public FileSystemMacroStorageWatcherTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "Macros.Tests.Watcher",
            Guid.NewGuid().ToString("N"));
        _globalRoot = Path.Combine(_tempRoot, "global");
        _namedFolder = Path.Combine(_globalRoot, "Macros");
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
            // Best-effort teardown.
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────

    private FileSystemMacroStorage CreateStorage()
    {
        // Create the named folder so the watcher can start immediately on subscription.
        Directory.CreateDirectory(_namedFolder);
        return new FileSystemMacroStorage(_globalRoot);
    }

    private static List<MacroLibraryChangedEventArgs> Capture(
        FileSystemMacroStorage storage,
        out EventHandler<MacroLibraryChangedEventArgs> handler)
    {
        var events = new List<MacroLibraryChangedEventArgs>();
        handler = (_, e) => { lock (events) { events.Add(e); } };
        storage.LibraryChanged += handler;
        return events;
    }

    private static string MacroFile(string folder, string name) =>
        Path.Combine(folder, name + ".csx");

    /// <summary>Waits up to <paramref name="timeoutMs"/> for <paramref name="predicate"/> to be satisfied.</summary>
    private static bool WaitFor(Func<bool> predicate, int timeoutMs = 600)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            Thread.Sleep(30);
        }
        return predicate();
    }

    // ── tests ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ExternalWrite_RaisesAdded()
    {
        using var storage = CreateStorage();
        var events = Capture(storage, out _);

        File.WriteAllText(MacroFile(_namedFolder, "Alpha"), "// alpha");

        Assert.True(WaitFor(() => { lock (events) return events.Count > 0; }),
            "Expected LibraryChanged(Added) event within timeout.");

        lock (events)
        {
            var e = Assert.Single(events);
            Assert.Equal(MacroLibraryChangeKind.Added, e.Kind);
            Assert.Equal("Alpha", e.Name);
            Assert.Equal(MacroScope.Global, e.Scope);
        }
    }

    [Fact]
    public void ExternalDelete_RaisesRemoved()
    {
        using var storage = CreateStorage();
        var path = MacroFile(_namedFolder, "Beta");
        File.WriteAllText(path, "// beta");
        Thread.Sleep(300); // let any Added event fire and settle

        var events = Capture(storage, out _);
        File.Delete(path);

        Assert.True(WaitFor(() => { lock (events) return events.Count > 0; }),
            "Expected LibraryChanged(Removed) event within timeout.");

        lock (events)
        {
            var e = Assert.Single(events);
            Assert.Equal(MacroLibraryChangeKind.Removed, e.Kind);
            Assert.Equal("Beta", e.Name);
        }
    }

    [Fact]
    public void ExternalRename_RaisesRemovedThenAdded()
    {
        using var storage = CreateStorage();
        var oldPath = MacroFile(_namedFolder, "Gamma");
        var newPath = MacroFile(_namedFolder, "GammaRenamed");
        File.WriteAllText(oldPath, "// gamma");
        Thread.Sleep(300);

        var events = Capture(storage, out _);
        File.Move(oldPath, newPath);

        Assert.True(WaitFor(() => { lock (events) return events.Count >= 2; }, 800),
            "Expected two LibraryChanged events (Removed + Added) within timeout.");

        lock (events)
        {
            Assert.Equal(2, events.Count);
            Assert.Contains(events, e => e.Kind == MacroLibraryChangeKind.Removed && e.Name == "Gamma");
            Assert.Contains(events, e => e.Kind == MacroLibraryChangeKind.Added && e.Name == "GammaRenamed");
        }
    }

    [Fact]
    public void ExternalModify_RaisesModified()
    {
        using var storage = CreateStorage();
        var path = MacroFile(_namedFolder, "Delta");
        File.WriteAllText(path, "// delta v1");
        Thread.Sleep(300);

        var events = Capture(storage, out _);
        File.WriteAllText(path, "// delta v2");

        Assert.True(WaitFor(() => { lock (events) return events.Count > 0; }),
            "Expected LibraryChanged(Modified) event within timeout.");

        lock (events)
        {
            Assert.Contains(events, e => e.Kind == MacroLibraryChangeKind.Modified && e.Name == "Delta");
        }
    }

    [Fact]
    public void RapidExternalChanges_AreDebouncedToOneEvent()
    {
        using var storage = CreateStorage();
        var path = MacroFile(_namedFolder, "Epsilon");
        File.WriteAllText(path, "// v1");
        Thread.Sleep(300);

        var events = Capture(storage, out _);

        // Fire several rapid changes — should coalesce into a single event.
        for (int i = 2; i <= 5; i++)
        {
            File.WriteAllText(path, $"// v{i}");
        }

        Thread.Sleep(500); // longer than debounce window

        lock (events)
        {
            // All changes to the same path collapse into at most one event per debounce window.
            Assert.True(events.Count == 1, $"Expected 1 debounced event, got {events.Count}.");
        }
    }

    [Fact]
    public async Task SelfWrite_OnlyOneLibraryChangedEvent()
    {
        using var storage = CreateStorage();
        var events = Capture(storage, out _);

        // SaveAsAsync raises LibraryChanged synchronously and also registers the self-write
        // to suppress the subsequent watcher event.
        await storage.SaveAsAsync("Zeta", "// zeta", MacroScope.Global);

        // Wait well past the debounce window so any duplicate watcher event would have fired.
        await Task.Delay(500);

        lock (events)
        {
            Assert.Single(events); // exactly the synchronous one; watcher event suppressed
            Assert.Equal(MacroLibraryChangeKind.Added, events[0].Kind);
            Assert.Equal("Zeta", events[0].Name);
        }
    }

    [Fact]
    public async Task SelfDelete_OnlyOneLibraryChangedEvent()
    {
        using var storage = CreateStorage();
        await storage.SaveAsAsync("Eta", "// eta", MacroScope.Global);

        var events = Capture(storage, out _);
        await storage.DeleteAsync("Eta", MacroScope.Global);

        await Task.Delay(500);

        lock (events)
        {
            Assert.Single(events);
            Assert.Equal(MacroLibraryChangeKind.Removed, events[0].Kind);
        }
    }

    [Fact]
    public async Task SelfRename_OnlyOneLibraryChangedEvent()
    {
        using var storage = CreateStorage();
        await storage.SaveAsAsync("Theta", "// theta", MacroScope.Global);

        var events = Capture(storage, out _);
        await storage.RenameAsync("Theta", "ThetaNew", MacroScope.Global);

        await Task.Delay(500);

        lock (events)
        {
            Assert.Single(events);
            Assert.Equal(MacroLibraryChangeKind.Renamed, events[0].Kind);
        }
    }

    [Fact]
    public void CurrentCsx_InNamedFolder_DoesNotFireEvent()
    {
        // Even if someone drops a "current.csx" inside the named subfolder, it must be
        // filtered out by OnFsEvent.
        using var storage = CreateStorage();
        var events = Capture(storage, out _);

        File.WriteAllText(Path.Combine(_namedFolder, "current.csx"), "// reserved");

        Thread.Sleep(400);

        lock (events)
        {
            Assert.Empty(events);
        }
    }

    [Fact]
    public void Dispose_StopsWatcher_NoMoreEvents()
    {
        var storage = CreateStorage();
        var events = Capture(storage, out _);

        storage.Dispose();

        // Write after disposal — no event should fire.
        File.WriteAllText(MacroFile(_namedFolder, "Iota"), "// iota");
        Thread.Sleep(400);

        lock (events)
        {
            Assert.Empty(events);
        }
    }
}
