using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine;
using Macros.Engine.Storage;
using Macros.ToolWindows;
using Microsoft.VisualStudio.Threading;
using Xunit;

namespace Macros.Tests.Integration;

/// <summary>
/// Repro for the user-reported bug: after recording stops, the new macro is on disk and
/// opens in the editor, but the tool window does not show it. Hitting Refresh also
/// fails to surface it; only restarting VS does.
///
/// This test reproduces the exact production wiring (CompositeMacroStore over Global +
/// Repo, MacroService with storage, recording stop saving via SaveAsAsync) and asserts
/// that the post-save listing INCLUDES the new macro.
/// </summary>
public sealed class RecordingFlowReproTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _globalRoot;
    private readonly string _repoRoot;

    public RecordingFlowReproTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "Macros.Tests.RecordRepro", Guid.NewGuid().ToString("N"));
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
        catch { /* best-effort */ }
    }

    private static JoinableTaskFactory CreateJtf()
    {
#pragma warning disable VSSDK005
        return new JoinableTaskContext().Factory;
#pragma warning restore VSSDK005
    }

    [Fact]
    public async Task StopRecording_AfterSave_ListAsync_Global_IncludesNewMacro()
    {
        // Mirror production: Composite over Global + Repo. RepoFolderProvider returns a
        // path with no folder yet so the repo half is silently empty.
        var global = new GlobalMacroStore(_globalRoot);
        var repo = new RepoMacroStore(() => _repoRoot);
        using var composite = new CompositeMacroStore(global, repo, ownsChildren: true);

        // Subscribe to LibraryChanged the same way the tool window VM does.
        var events = new List<MacroLibraryChangedEventArgs>();
        composite.LibraryChanged += (_, e) => events.Add(e);

        // Construct the engine with the composite as its storage. This mirrors
        // MacrosPackage.InitializeAsync's wiring.
        var jtf = CreateJtf();
        var svc = new MacroService(jtf, maxStepsProvider: () => int.MaxValue, storage: composite);

        // Drive a recording stop (which fires-and-forgets a save through the JTF).
        await svc.StartRecordingAsync();
        var savedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.RecordingSaved += (_, e) => savedTcs.TrySetResult(e.Path);
        await svc.StopRecordingAsync();

        // Wait for the fire-and-forget save to complete.
        var winner = await Task.WhenAny(savedTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(savedTcs.Task, winner);
        var savedPath = await savedTcs.Task;

        // Sanity: file is on disk.
        Assert.True(File.Exists(savedPath), $"Saved file not on disk: {savedPath}");

        // CONTRACT 1: LibraryChanged fired through the composite.
        Assert.Contains(events, e =>
            e.Kind == MacroLibraryChangeKind.Added &&
            e.Scope == MacroScope.Global);

        // CONTRACT 2: ListAsync(Global) returns the new macro.
        var entries = await composite.ListAsync(MacroScope.Global);
        var savedName = Path.GetFileNameWithoutExtension(savedPath);
        Assert.Contains(entries, e => string.Equals(e.Name, savedName, StringComparison.OrdinalIgnoreCase));

        // CONTRACT 3: ListAllAsync also includes it (this is what the tool window calls).
        var all = await composite.ListAllAsync();
        Assert.Contains(all, e => string.Equals(e.Name, savedName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ToolWindowVM_AfterRecordingSave_ShowsNewMacro()
    {
        // Wire up a real composite and a tool window VM exactly the way production does.
        var global = new GlobalMacroStore(_globalRoot);
        var repo = new RepoMacroStore(() => null); // no solution
        using var composite = new CompositeMacroStore(global, repo, ownsChildren: true);

        var jtf = CreateJtf();
        var svc = new MacroService(jtf, maxStepsProvider: () => int.MaxValue, storage: composite);

        // VM with zero-debounce so we can observe reloads deterministically.
        using var vm = new MacrosToolWindowViewModel(
            composite,
            svc,
            uiSync: null,
            debounceInterval: TimeSpan.FromMilliseconds(0));

        // Initial load — empty.
        await vm.LoadAsync();
        var globalGroupBefore = vm.Groups.First(g => g.Scope == MacroScope.Global && !g.IsShadowed);
        Assert.Empty(globalGroupBefore.Items);

        // Set up the next-load handshake BEFORE we trigger the save so we don't miss it.
        var nextLoadTask = vm.NextLoadAsync();

        // Run a record + stop cycle; the engine's fire-and-forget save will hit the
        // composite, fire LibraryChanged, schedule the VM debounce, and reload.
        var savedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.RecordingSaved += (_, e) => savedTcs.TrySetResult(e.Path);
        await svc.StartRecordingAsync();
        await svc.StopRecordingAsync();

        // Wait for the disk save (fire-and-forget) AND the VM reload.
        var savedWinner = await Task.WhenAny(savedTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(savedTcs.Task, savedWinner);
        string savedPath = await savedTcs.Task;
        string savedName = Path.GetFileNameWithoutExtension(savedPath);

        var loadWinner = await Task.WhenAny(nextLoadTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(nextLoadTask, loadWinner);

        // The VM's Global group must now contain the just-recorded macro.
        var globalGroup = vm.Groups.First(g => g.Scope == MacroScope.Global && !g.IsShadowed);
        Assert.Contains(globalGroup.Items, item => string.Equals(item.Name, savedName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ToolWindowVM_AfterRecordingSave_WithRealisticContext_ShowsNewMacro()
    {
        // Production-realistic harness: use the production default debounce (100ms), a real
        // SynchronizationContext that posts to a worker queue, and assert that LibraryChanged
        // → debounce timer → marshal → LoadAsync end-to-end actually surfaces the new macro
        // without anyone calling Refresh.
        var global = new GlobalMacroStore(_globalRoot);
        var repo = new RepoMacroStore(() => null);
        using var composite = new CompositeMacroStore(global, repo, ownsChildren: true);

        var jtf = CreateJtf();
        var svc = new MacroService(jtf, maxStepsProvider: () => int.MaxValue, storage: composite);

        // A simple pumping SynchronizationContext that drains queued callbacks on a single
        // dedicated thread. Mirrors the WPF dispatcher's "single-thread-affinity + Post-from-
        // anywhere" behaviour without requiring an actual WPF Dispatcher.
        using var pump = new PumpingSyncContext();

        using var vm = new MacrosToolWindowViewModel(
            composite,
            svc,
            uiSync: pump.Context,
            debounceInterval: TimeSpan.FromMilliseconds(100));

        await vm.LoadAsync();
        Assert.Empty(vm.Groups.First(g => g.Scope == MacroScope.Global && !g.IsShadowed).Items);

        var nextLoadTask = vm.NextLoadAsync();

        var savedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.RecordingSaved += (_, e) => savedTcs.TrySetResult(e.Path);
        await svc.StartRecordingAsync();
        await svc.StopRecordingAsync();

        var savedWinner = await Task.WhenAny(savedTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(savedTcs.Task, savedWinner);
        string savedPath = await savedTcs.Task;
        string savedName = Path.GetFileNameWithoutExtension(savedPath);

        var loadWinner = await Task.WhenAny(nextLoadTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(nextLoadTask, loadWinner);

        var globalGroup = vm.Groups.First(g => g.Scope == MacroScope.Global && !g.IsShadowed);
        Assert.Contains(globalGroup.Items, item => string.Equals(item.Name, savedName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Single-threaded synchronization context that pumps Post() callbacks on a dedicated
    /// worker thread. Used by tests that need to exercise the production path of
    /// MacrosToolWindowViewModel.Marshal(...) without dragging in the WPF Dispatcher.
    /// </summary>
    private sealed class PumpingSyncContext : IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly Thread _worker;
        public SynchronizationContext Context { get; }

        public PumpingSyncContext()
        {
            Context = new ForwardingContext(this);
            _worker = new Thread(WorkerLoop) { IsBackground = true };
            _worker.Start();
        }

        public void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        private void WorkerLoop()
        {
            SynchronizationContext.SetSynchronizationContext(Context);
            foreach (var (cb, state) in _queue.GetConsumingEnumerable())
            {
                try { cb(state); } catch { /* swallowed — test infra */ }
            }
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _worker.Join(TimeSpan.FromSeconds(2));
            _queue.Dispose();
        }

        private sealed class ForwardingContext : SynchronizationContext
        {
            private readonly PumpingSyncContext _owner;
            public ForwardingContext(PumpingSyncContext owner) { _owner = owner; }
            public override void Post(SendOrPostCallback d, object? state) => _owner.Post(d, state);
            public override void Send(SendOrPostCallback d, object? state) => d(state);
        }
    }

    [Fact]
    public async Task ToolWindowVM_LoadAsync_AfterDirectSaveAsAsync_ShowsNewMacro()
    {
        // This is the "pure refresh" path: bypass the engine entirely, save directly via
        // the storage, then call vm.LoadAsync() (what RefreshCommand does).
        var global = new GlobalMacroStore(_globalRoot);
        var repo = new RepoMacroStore(() => null);
        using var composite = new CompositeMacroStore(global, repo, ownsChildren: true);

        using var vm = new MacrosToolWindowViewModel(
            composite,
            service: null,
            uiSync: null,
            debounceInterval: TimeSpan.FromMilliseconds(0));

        await vm.LoadAsync();
        var groupBefore = vm.Groups.First(g => g.Scope == MacroScope.Global && !g.IsShadowed);
        Assert.Empty(groupBefore.Items);

        // Save directly via the composite. This is exactly the call MacroService makes.
        await composite.SaveAsAsync("Refreshed", "// fresh\n", MacroScope.Global, overwrite: false);

        // Refresh = LoadAsync.
        await vm.LoadAsync();

        var group = vm.Groups.First(g => g.Scope == MacroScope.Global && !g.IsShadowed);
        Assert.Contains(group.Items, item => item.Name == "Refreshed");
    }
}

