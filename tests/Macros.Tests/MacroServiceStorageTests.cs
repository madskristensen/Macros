using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine;
using Macros.Engine.Player;
using Macros.Engine.Storage;
using Microsoft.VisualStudio.Threading;
using Xunit;

namespace Macros.Tests;

/// <summary>
/// Verifies that the optional <see cref="IMacroStore"/> dependency on
/// <see cref="MacroService"/> is wired correctly: stop-recording persists the generated
/// source fire-and-forget, play-current rehydrates from disk on a fresh service, and a
/// service constructed without storage still works (the unit-test default).
/// </summary>
public sealed class MacroServiceStorageTests
{
    private static JoinableTaskFactory CreateJtf()
    {
#pragma warning disable VSSDK005
        return new JoinableTaskContext().Factory;
#pragma warning restore VSSDK005
    }

    [Fact]
    public async Task StopRecordingAsync_WithStorage_PersistsGeneratedSource()
    {
        var fake = new FakeMacroStorage();
        var svc = new MacroService(CreateJtf(), storage: fake);

        await svc.StartRecordingAsync();
        var source = await svc.StopRecordingAsync();

        // The save is fire-and-forget through JTF.RunAsync; allow it to settle. With no UI
        // thread in tests, the run-async work executes on the thread pool synchronously
        // enough that a single yield is sufficient, but we wait on the fake's signal to
        // be robust against scheduler timing.
        await fake.WaitForSaveAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(source, fake.LastSaved);
    }

    [Fact]
    public async Task StopRecordingAsync_WithStorage_AlsoSavesToNamedLibrary()
    {
        // Regression guard: Stop Recording must call SaveAsAsync in addition to
        // SaveCurrentAsync so that IMacroStore.LibraryChanged fires and the Macros tool
        // window refreshes its list. Without the SaveAsAsync call current.csx is excluded
        // from ListAsync and the tool window never sees the just-recorded macro.
        var fake = new FakeMacroStorage();
        var svc = new MacroService(CreateJtf(), storage: fake);

        await svc.StartRecordingAsync();
        var source = await svc.StopRecordingAsync();

        await fake.WaitForNamedSaveAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("RecordedMacro1", fake.LastSavedName);
        Assert.Equal(MacroScope.Global, fake.LastSavedScope);
        Assert.Equal(source, fake.LastSavedNamedSource);
    }

    [Fact]
    public async Task PlayCurrentAsync_RehydratesFromStorage_AfterRestart()
    {
        // Simulate "VS restarted": pre-populate storage but construct a fresh MacroService
        // that has never recorded. Verify PlayCurrentAsync loads the persisted source and
        // hands it to the injected player.
        const string persisted = "// persisted from previous session\n";
        var fake = new FakeMacroStorage();
        await fake.SaveCurrentAsync(persisted);

        string? observedSource = null;
        var player = new CapturingMacroPlayer(src => observedSource = src);
        var svc = new MacroService(
            CreateJtf(),
            playerFactory: _ => Task.FromResult<IMacroPlayer>(player),
            storage: fake);

        Assert.Null(svc.CurrentMacroSource);

        var result = await svc.PlayCurrentAsync();

        Assert.True(result.Success);
        Assert.Equal(persisted, observedSource);
        Assert.Equal(persisted, svc.CurrentMacroSource);
    }

    [Fact]
    public async Task PlayCurrentAsync_NoStorage_AndNoSource_ReturnsSyntheticFailure()
    {
        var svc = new MacroService(CreateJtf());

        var result = await svc.PlayCurrentAsync();

        Assert.False(result.Success);
        Assert.NotNull(result.CompilationError);
    }

    [Fact]
    public async Task PlayCurrentAsync_StorageEmpty_ReturnsSyntheticFailure()
    {
        var fake = new FakeMacroStorage();
        var svc = new MacroService(CreateJtf(), storage: fake);

        var result = await svc.PlayCurrentAsync();

        Assert.False(result.Success);
    }

    [Fact]
    public async Task PlayCurrentAsync_InMemorySource_WinsOverStorage()
    {
        // Once the engine has a fresh in-memory recording, that wins — it's strictly newer
        // than what's on disk (the disk write may even still be in flight).
        var fake = new FakeMacroStorage();
        await fake.SaveCurrentAsync("// stale on-disk content\n");

        string? observedSource = null;
        var player = new CapturingMacroPlayer(src => observedSource = src);
        var svc = new MacroService(
            CreateJtf(),
            playerFactory: _ => Task.FromResult<IMacroPlayer>(player),
            storage: fake);

        await svc.StartRecordingAsync();
        var freshSource = await svc.StopRecordingAsync();

        await svc.PlayCurrentAsync();

        Assert.Equal(freshSource, observedSource);
        Assert.NotEqual("// stale on-disk content\n", observedSource);
    }

    [Fact]
    public void CurrentMacroPath_NoStorage_IsNull()
    {
        var svc = new MacroService(CreateJtf());

        Assert.Null(svc.CurrentMacroPath);
    }

    [Fact]
    public void CurrentMacroPath_WithStorage_ExposesStorageCurrentPath()
    {
        var fake = new FakeMacroStorage();
        var svc = new MacroService(CreateJtf(), storage: fake);

        Assert.Equal(fake.CurrentPath, svc.CurrentMacroPath);
    }

    [Fact]
    public async Task StopRecordingAsync_SaveFailure_DoesNotPropagate()
    {
        // Fire-and-forget contract: a failing storage write must not throw out of
        // StopRecordingAsync, and the in-memory source must still be available.
        var fake = new FakeMacroStorage(throwOnSave: true);
        var svc = new MacroService(CreateJtf(), storage: fake);

        await svc.StartRecordingAsync();
        var source = await svc.StopRecordingAsync();

        Assert.NotNull(source);
        Assert.Equal(source, svc.CurrentMacroSource);
    }

    // ─── GenerateUniqueRecordingName tests ──────────────────────────────────────────────

    [Fact]
    public async Task StopRecordingAsync_EmptyLibrary_SavesAsRecordedMacro1()
    {
        var fake = new FakeMacroStorage(libraryNames: Array.Empty<string>());
        var svc = new MacroService(CreateJtf(), storage: fake);

        await svc.StartRecordingAsync();
        await svc.StopRecordingAsync();
        await fake.WaitForNamedSaveAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("RecordedMacro1", fake.LastSavedName);
    }

    [Fact]
    public async Task StopRecordingAsync_LibraryHasRecordedMacroNoNumber_SavesAsRecordedMacro1()
    {
        // "RecordedMacro" (no suffix) is treated as N=0; highest+1 → 1.
        var fake = new FakeMacroStorage(libraryNames: new[] { "RecordedMacro" });
        var svc = new MacroService(CreateJtf(), storage: fake);

        await svc.StartRecordingAsync();
        await svc.StopRecordingAsync();
        await fake.WaitForNamedSaveAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("RecordedMacro1", fake.LastSavedName);
    }

    [Fact]
    public async Task StopRecordingAsync_LibraryHasRecordedMacro1_SavesAsRecordedMacro2()
    {
        var fake = new FakeMacroStorage(libraryNames: new[] { "RecordedMacro1" });
        var svc = new MacroService(CreateJtf(), storage: fake);

        await svc.StartRecordingAsync();
        await svc.StopRecordingAsync();
        await fake.WaitForNamedSaveAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("RecordedMacro2", fake.LastSavedName);
    }

    [Fact]
    public async Task StopRecordingAsync_LibraryHasGap_SavesAsHighestPlusOne()
    {
        // Has RecordedMacro1 and RecordedMacro3 (gap at 2); highest+1 rule → 4, not 2.
        var fake = new FakeMacroStorage(libraryNames: new[] { "RecordedMacro1", "RecordedMacro3" });
        var svc = new MacroService(CreateJtf(), storage: fake);

        await svc.StartRecordingAsync();
        await svc.StopRecordingAsync();
        await fake.WaitForNamedSaveAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("RecordedMacro4", fake.LastSavedName);
    }

    [Fact]
    public async Task StopRecordingAsync_LibraryHasRecordedMacro7_SavesAsRecordedMacro8()
    {
        var fake = new FakeMacroStorage(libraryNames: new[] { "RecordedMacro7" });
        var svc = new MacroService(CreateJtf(), storage: fake);

        await svc.StartRecordingAsync();
        await svc.StopRecordingAsync();
        await fake.WaitForNamedSaveAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("RecordedMacro8", fake.LastSavedName);
    }

    [Fact]
    public async Task StopRecordingAsync_LibraryHasOnlyUnrelatedMacros_SavesAsRecordedMacro1()
    {
        var fake = new FakeMacroStorage(libraryNames: new[] { "MyMacro", "Build" });
        var svc = new MacroService(CreateJtf(), storage: fake);

        await svc.StartRecordingAsync();
        await svc.StopRecordingAsync();
        await fake.WaitForNamedSaveAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("RecordedMacro1", fake.LastSavedName);
    }

    [Fact]
    public async Task StopRecordingAsync_RaisesRecordingSaved_WithAbsolutePath()
    {
        var fake = new FakeMacroStorage();
        var svc = new MacroService(CreateJtf(), storage: fake);

        string? observedPath = null;
        var tcs = new TaskCompletionSource<string>();
        svc.RecordingSaved += (_, args) =>
        {
            observedPath = args.Path;
            tcs.TrySetResult(args.Path);
        };

        await svc.StartRecordingAsync();
        await svc.StopRecordingAsync();

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Equal(tcs.Task, completed);

        Assert.NotNull(observedPath);
        // FakeMacroStorage.GetMacroPath returns X:\fake\{name}.csx
        Assert.Equal(@"X:\fake\RecordedMacro1.csx", observedPath);
    }

    [Fact]
    public async Task StopRecordingAsync_RecordingSaved_FiresOnlyAfterSaveAsCompletes()
    {
        // Verify sequencing: when RecordingSaved fires, SaveAs has already committed
        // (fake.LastSavedName is populated before the event handler runs).
        var fake = new FakeMacroStorage();
        var svc = new MacroService(CreateJtf(), storage: fake);

        string? savedNameAtEventTime = null;
        var tcs = new TaskCompletionSource<bool>();
        svc.RecordingSaved += (_, _) =>
        {
            savedNameAtEventTime = fake.LastSavedName;
            tcs.TrySetResult(true);
        };

        await svc.StartRecordingAsync();
        await svc.StopRecordingAsync();

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Equal(tcs.Task, completed);

        Assert.Equal("RecordedMacro1", savedNameAtEventTime);
    }

    /// <summary>In-memory <see cref="IMacroStore"/> double for engine wiring tests.</summary>
    private sealed class FakeMacroStorage : IMacroStore
    {
        private readonly bool _throwOnSave;
        private readonly TaskCompletionSource<string> _saveTcs = new();
        private readonly TaskCompletionSource<string> _namedSaveTcs = new();
        private string? _content;
        private readonly List<string> _libraryNames;

        public FakeMacroStorage(bool throwOnSave = false, IEnumerable<string>? libraryNames = null)
        {
            _throwOnSave = throwOnSave;
            _libraryNames = libraryNames is not null ? new List<string>(libraryNames) : new List<string>();
        }

        public string CurrentPath => @"X:\fake\current.csx";

        public string? LastSaved => _content;
        public string? LastSavedName { get; private set; }
        public MacroScope? LastSavedScope { get; private set; }
        public string? LastSavedNamedSource { get; private set; }

        public Task SaveCurrentAsync(string source, CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();

            if (_throwOnSave)
            {
                return Task.FromException(new System.IO.IOException("simulated"));
            }

            _content = source;
            _saveTcs.TrySetResult(source);
            return Task.CompletedTask;
        }

        public Task<string?> LoadCurrentAsync(CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            return Task.FromResult(_content);
        }

        public Task<bool> DeleteCurrentAsync(CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            var existed = _content is not null;
            _content = null;
            return Task.FromResult(existed);
        }

        // ─── Named-macro API: not exercised by these wiring tests; trivial stubs. ──────

        public event EventHandler<MacroLibraryChangedEventArgs>? LibraryChanged
        {
            add { /* no-op for fake */ }
            remove { /* no-op for fake */ }
        }

        public Task<IReadOnlyList<MacroEntry>> ListAsync(MacroScope scope, CancellationToken cancellation = default)
        {
            var entries = _libraryNames
                .Select(n => new MacroEntry(n, scope, $@"X:\fake\{n}.csx", 0, DateTimeOffset.UtcNow, 0, Array.Empty<Macros.Engine.Triggers.TriggerBinding>()))
                .ToArray();
            return Task.FromResult<IReadOnlyList<MacroEntry>>(entries);
        }

        public Task<IReadOnlyList<MacroEntry>> ListAllAsync(CancellationToken cancellation = default)
            => ListAsync(MacroScope.Global, cancellation);

        public Task<string?> LoadByNameAsync(string name, MacroScope scope, CancellationToken cancellation = default)
            => Task.FromResult<string?>(null);

        public Task SaveAsAsync(string name, string source, MacroScope scope, bool overwrite = false, CancellationToken cancellation = default)
        {
            LastSavedName = name;
            LastSavedScope = scope;
            LastSavedNamedSource = source;
            _libraryNames.Add(name);
            _namedSaveTcs.TrySetResult(name);
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string name, MacroScope scope, CancellationToken cancellation = default)
            => Task.FromResult(false);

        public Task RenameAsync(string oldName, string newName, MacroScope scope, CancellationToken cancellation = default)
            => Task.CompletedTask;

        public string GetMacroPath(string name, MacroScope scope) => $"X:\\fake\\{name}.csx";

        public bool IsValidName(string name) => !string.IsNullOrWhiteSpace(name);

        public Task<MacroEntry?> RefreshEntryAsync(string name, MacroScope scope, CancellationToken cancellation = default)
            => Task.FromResult<MacroEntry?>(null);

        public async Task WaitForSaveAsync(TimeSpan timeout)
        {
            var completed = await Task.WhenAny(_saveTcs.Task, Task.Delay(timeout));
            if (completed != _saveTcs.Task)
            {
                throw new TimeoutException("Storage save did not occur within the expected window.");
            }
        }

        public async Task WaitForNamedSaveAsync(TimeSpan timeout)
        {
            var completed = await Task.WhenAny(_namedSaveTcs.Task, Task.Delay(timeout));
            if (completed != _namedSaveTcs.Task)
            {
                throw new TimeoutException("Named-library save did not occur within the expected window.");
            }
        }
    }

    /// <summary>Records the source string the engine hands to the player.</summary>
    private sealed class CapturingMacroPlayer : IMacroPlayer
    {
        private readonly Action<string> _capture;

        public CapturingMacroPlayer(Action<string> capture)
        {
            _capture = capture;
        }

        public Task<MacroPlayResult> PlayAsync(string source, string macroName, Macros.Engine.Triggers.IMacroTrigger? trigger, CancellationToken cancellation, string? csxFilePath = null)
        {
            _capture(source);
            return Task.FromResult(new MacroPlayResult(true, null, null, TimeSpan.Zero));
        }
    }
}
