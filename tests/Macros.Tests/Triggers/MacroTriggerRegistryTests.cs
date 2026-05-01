using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine.Storage;
using Macros.Engine.Triggers;
using Microsoft.VisualStudio.Threading;
using Xunit;

namespace Macros.Tests.Triggers;

/// <summary>
/// Pins the contract of <see cref="MacroTriggerRegistry"/>: index population from the macro
/// store, debounced refresh on <see cref="IMacroStore.LibraryChanged"/>, the kill-switch
/// short-circuit, and the unsubscribe-on-Dispose hygiene.
/// </summary>
public sealed class MacroTriggerRegistryTests
{
    private static JoinableTaskFactory CreateJtf()
    {
#pragma warning disable VSSDK005 // Use ThreadHelper.JoinableTaskContext — not available in unit tests.
        return new JoinableTaskContext().Factory;
#pragma warning restore VSSDK005
    }

    private static MacroEntry MakeEntry(
        string name,
        params TriggerBinding[] triggers)
        => new(
            name,
            MacroScope.Global,
            $"X:\\fake\\{name}.csx",
            StepCount: 0,
            Modified: DateTimeOffset.UtcNow,
            SizeBytes: 0,
            Triggers: triggers.Length == 0
                ? new[] { TriggerBinding.Manual }
                : triggers);

    [Fact]
    public void New_Registry_IsLoaded_False_Initially()
    {
        // Block ListAllAsync forever so the fire-and-forget initial load never completes —
        // proves IsLoaded is the result of a refresh, not just construction.
        var store = new FakeStore();
        store.BlockListAll = true;

        using var registry = new MacroTriggerRegistry(store, CreateJtf());

        Assert.False(registry.IsLoaded);
        Assert.Empty(registry.FindByEvent("Build.SolutionBuildDone"));
        Assert.Empty(registry.FindByBeforeCommand("File.Save"));
        Assert.Empty(registry.FindByAfterCommand("File.Save"));
        Assert.False(registry.HasBeforeCommand("File.Save"));
        Assert.False(registry.HasAfterCommand("File.Save"));
    }

    [Fact]
    public async Task RefreshAsync_EmptyStore_MarksLoaded_AndAllQueriesEmpty()
    {
        var store = new FakeStore();
        using var registry = new MacroTriggerRegistry(store, CreateJtf());

        await registry.RefreshAsync();

        Assert.True(registry.IsLoaded);
        Assert.Empty(registry.FindByEvent("Build.SolutionBuildDone"));
        Assert.Empty(registry.FindByBeforeCommand("File.Save"));
        Assert.Empty(registry.FindByAfterCommand("File.Save"));
        Assert.False(registry.HasBeforeCommand("File.Save"));
        Assert.False(registry.HasAfterCommand("File.Save"));
    }

    [Fact]
    public async Task RefreshAsync_PopulatedStore_IndexesByEventAndCommand()
    {
        var store = new FakeStore();
        store.Entries.Add(MakeEntry(
            "OnBuildDone",
            new TriggerBinding(TriggerKind.VsEvent, "Build.SolutionBuildDone")));
        store.Entries.Add(MakeEntry(
            "AroundFileSave",
            new TriggerBinding(TriggerKind.BeforeCommand, "File.Save"),
            new TriggerBinding(TriggerKind.AfterCommand, "Build.BuildSolution")));
        store.Entries.Add(MakeEntry("ManualOnly")); // Manual default

        using var registry = new MacroTriggerRegistry(store, CreateJtf());
        await registry.RefreshAsync();

        // Event index
        var build = registry.FindByEvent("Build.SolutionBuildDone");
        Assert.Single(build);
        Assert.Equal("OnBuildDone", build[0].Entry.Name);
        Assert.Equal(TriggerKind.VsEvent, build[0].Binding.Kind);

        Assert.Empty(registry.FindByEvent("Document.Saved"));

        // Command indexes
        var before = registry.FindByBeforeCommand("File.Save");
        Assert.Single(before);
        Assert.Equal("AroundFileSave", before[0].Entry.Name);

        var after = registry.FindByAfterCommand("Build.BuildSolution");
        Assert.Single(after);
        Assert.Equal("AroundFileSave", after[0].Entry.Name);

        // Hot-path booleans
        Assert.True(registry.HasBeforeCommand("File.Save"));
        Assert.False(registry.HasBeforeCommand("Edit.Cut"));
        Assert.True(registry.HasAfterCommand("Build.BuildSolution"));
        Assert.False(registry.HasAfterCommand("File.Save"));

        // Manual binding contributes nothing to any of the dispatch indexes.
        Assert.Empty(registry.FindByEvent("Manual"));
        Assert.False(registry.HasBeforeCommand("Manual"));
    }

    [Fact]
    public async Task Lookups_AreCaseInsensitive_ForEventAndCommandNames()
    {
        var store = new FakeStore();
        store.Entries.Add(MakeEntry(
            "Mixed",
            new TriggerBinding(TriggerKind.VsEvent, "Build.SolutionBuildDone"),
            new TriggerBinding(TriggerKind.BeforeCommand, "File.Save")));

        using var registry = new MacroTriggerRegistry(store, CreateJtf());
        await registry.RefreshAsync();

        Assert.Single(registry.FindByEvent("build.solutionbuilddone"));
        Assert.Single(registry.FindByBeforeCommand("FILE.SAVE"));
        Assert.True(registry.HasBeforeCommand("file.save"));
    }

    [Fact]
    public async Task LibraryChanged_TriggersDebouncedRefresh()
    {
        var store = new FakeStore();
        using var registry = new MacroTriggerRegistry(store, CreateJtf());
        await registry.RefreshAsync();

        // Initially no Build.SolutionBuildDone bindings.
        Assert.Empty(registry.FindByEvent("Build.SolutionBuildDone"));

        // Mutate the store, raise LibraryChanged, then wait past the debounce window.
        store.Entries.Add(MakeEntry(
            "Late",
            new TriggerBinding(TriggerKind.VsEvent, "Build.SolutionBuildDone")));
        var refreshSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.Changed += (_, _) => refreshSeen.TrySetResult(true);

        store.RaiseLibraryChanged(new MacroLibraryChangedEventArgs(
            MacroLibraryChangeKind.Added, MacroScope.Global, "Late"));

        // Wait for the debounced refresh (100 ms + jitter) to fire Changed.
        var completed = await Task.WhenAny(refreshSeen.Task, Task.Delay(2000));
        Assert.Same(refreshSeen.Task, completed);

        var matches = registry.FindByEvent("Build.SolutionBuildDone");
        Assert.Single(matches);
        Assert.Equal("Late", matches[0].Entry.Name);
    }

    [Fact]
    public async Task DisableAllTriggers_ShortCircuitsAllQueries_RegistryStaysLoaded()
    {
        var killSwitch = false;
        var store = new FakeStore();
        store.Entries.Add(MakeEntry(
            "Hot",
            new TriggerBinding(TriggerKind.VsEvent, "Build.SolutionBuildDone"),
            new TriggerBinding(TriggerKind.BeforeCommand, "File.Save"),
            new TriggerBinding(TriggerKind.AfterCommand, "File.Save")));

        using var registry = new MacroTriggerRegistry(
            store,
            CreateJtf(),
            disableAllTriggersProvider: () => killSwitch);

        await registry.RefreshAsync();

        // Sanity: triggers visible while flag is off.
        Assert.True(registry.IsLoaded);
        Assert.Single(registry.FindByEvent("Build.SolutionBuildDone"));
        Assert.True(registry.HasBeforeCommand("File.Save"));

        // Flip the kill-switch — no reload required.
        killSwitch = true;

        Assert.True(registry.IsLoaded, "Registry stays loaded even when kill-switch is on.");
        Assert.Empty(registry.FindByEvent("Build.SolutionBuildDone"));
        Assert.Empty(registry.FindByBeforeCommand("File.Save"));
        Assert.Empty(registry.FindByAfterCommand("File.Save"));
        Assert.False(registry.HasBeforeCommand("File.Save"));
        Assert.False(registry.HasAfterCommand("File.Save"));

        // Flip it back — indexes resume immediately.
        killSwitch = false;
        Assert.Single(registry.FindByEvent("Build.SolutionBuildDone"));
        Assert.True(registry.HasBeforeCommand("File.Save"));
    }

    [Fact]
    public async Task BurstOfLibraryChanges_CoalescesIntoSingleRefresh()
    {
        var store = new FakeStore();
        using var registry = new MacroTriggerRegistry(store, CreateJtf());
        await registry.RefreshAsync();

        // Reset call count after the explicit refresh above so we count only debounced work.
        var baseline = store.ListAllCallCount;

        // Fire 5 events back-to-back; the 100 ms debounce should collapse them into one
        // store enumeration (or at most two if scheduling slips, but never five).
        for (var i = 0; i < 5; i++)
        {
            store.RaiseLibraryChanged(new MacroLibraryChangedEventArgs(
                MacroLibraryChangeKind.Added, MacroScope.Global, $"M{i}"));
        }

        // Wait long enough for the debounce window to lapse and the refresh to complete.
        await Task.Delay(500);

        var added = store.ListAllCallCount - baseline;
        Assert.True(
            added <= 2,
            $"Expected debounce to coalesce burst into ≤2 refreshes; observed {added}.");
        Assert.True(added >= 1, "Expected at least one refresh from the burst.");
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromLibraryChanged()
    {
        var store = new FakeStore();
        var registry = new MacroTriggerRegistry(store, CreateJtf());
        await registry.RefreshAsync();

        Assert.Equal(1, store.LibraryChangedSubscriberCount);

        registry.Dispose();

        Assert.Equal(0, store.LibraryChangedSubscriberCount);

        // After disposal, raising LibraryChanged is a no-op — the registry won't schedule
        // another refresh and won't throw.
        var before = store.ListAllCallCount;
        store.RaiseLibraryChanged(new MacroLibraryChangedEventArgs(
            MacroLibraryChangeKind.Added, MacroScope.Global, "Ghost"));
        await Task.Delay(300);
        Assert.Equal(before, store.ListAllCallCount);
    }

    [Fact]
    public async Task RefreshAsync_AfterDispose_DoesNotThrow_AndDoesNotCallStore()
    {
        var store = new FakeStore();
        var registry = new MacroTriggerRegistry(store, CreateJtf());
        await registry.RefreshAsync();

        var baseline = store.ListAllCallCount;
        registry.Dispose();

        await registry.RefreshAsync();
        Assert.Equal(baseline, store.ListAllCallCount);
    }

    /// <summary>
    /// Minimal in-memory <see cref="IMacroStore"/> double for registry tests. Only the
    /// enumeration + LibraryChanged surface is exercised; everything else throws so a
    /// future caller misuse fails loudly rather than returning silent defaults.
    /// </summary>
    private sealed class FakeStore : IMacroStore
    {
        public List<MacroEntry> Entries { get; } = new();
        public bool BlockListAll { get; set; }
        public int ListAllCallCount;
        public int LibraryChangedSubscriberCount { get; private set; }

        private EventHandler<MacroLibraryChangedEventArgs>? _changed;
        public event EventHandler<MacroLibraryChangedEventArgs>? LibraryChanged
        {
            add { _changed += value; LibraryChangedSubscriberCount++; }
            remove { _changed -= value; LibraryChangedSubscriberCount--; }
        }

        public void RaiseLibraryChanged(MacroLibraryChangedEventArgs args)
            => _changed?.Invoke(this, args);

        public string CurrentPath => "X:\\fake\\current.csx";

        public Task SaveCurrentAsync(string source, CancellationToken cancellation = default)
            => Task.CompletedTask;
        public Task<string?> LoadCurrentAsync(CancellationToken cancellation = default)
            => Task.FromResult<string?>(null);
        public Task<bool> DeleteCurrentAsync(CancellationToken cancellation = default)
            => Task.FromResult(false);

        public async Task<IReadOnlyList<MacroEntry>> ListAsync(
            MacroScope scope,
            CancellationToken cancellation = default)
        {
            await Task.Yield();
            return Entries.Where(e => e.Scope == scope).ToArray();
        }

        public async Task<IReadOnlyList<MacroEntry>> ListAllAsync(
            CancellationToken cancellation = default)
        {
            Interlocked.Increment(ref ListAllCallCount);
            if (BlockListAll)
            {
                // Park forever so the initial fire-and-forget load never completes.
                await Task.Delay(Timeout.Infinite, cancellation).ConfigureAwait(false);
            }
            await Task.Yield();
            return Entries.ToArray();
        }

        public Task<MacroEntry?> RefreshEntryAsync(
            string name, MacroScope scope, CancellationToken cancellation = default)
            => Task.FromResult<MacroEntry?>(null);

        public Task<string?> LoadByNameAsync(
            string name, MacroScope scope, CancellationToken cancellation = default)
            => Task.FromResult<string?>(null);

        public Task SaveAsAsync(
            string name, string source, MacroScope scope,
            bool overwrite = false, CancellationToken cancellation = default)
            => Task.CompletedTask;

        public Task<bool> DeleteAsync(
            string name, MacroScope scope, CancellationToken cancellation = default)
            => Task.FromResult(false);

        public Task RenameAsync(
            string oldName, string newName, MacroScope scope, CancellationToken cancellation = default)
            => Task.CompletedTask;

        public string GetMacroPath(string name, MacroScope scope)
            => $"X:\\fake\\{scope}\\{name}.csx";

        public bool IsValidName(string name) => !string.IsNullOrWhiteSpace(name);
    }
}
