using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine.Storage;
using Macros.Engine.Triggers;
using Microsoft.VisualStudio.Threading;
using Xunit;

namespace Macros.Tests.Integration;

/// <summary>
/// End-to-end integration tests for the auto-disable pipeline combining a real
/// <see cref="MacroTriggerRegistry"/> with a real <see cref="MacroFailureTracker"/> and
/// a fake in-memory <see cref="IMacroStore"/>. Covers the full lifecycle: trigger
/// indexing → failure tracking → filtered lookups → re-enable → streak reset.
/// </summary>
public sealed class AutoDisableIntegrationTests
{
#pragma warning disable VSSDK005
    private static JoinableTaskFactory CreateJtf()
        => new JoinableTaskContext().Factory;
#pragma warning restore VSSDK005

    // ─── Helpers ──────────────────────────────────────────────────────────────────────

    private static MacroEntry MakeEntry(string name, params TriggerBinding[] triggers) =>
        new(
            name,
            MacroScope.Repo,
            $@"X:\macros\{name}.csx",
            StepCount: 0,
            Modified: DateTimeOffset.UtcNow,
            SizeBytes: 0,
            Triggers: triggers.Length == 0
                ? new[] { TriggerBinding.Manual }
                : triggers);

    private static TriggerBinding VsEvent(string eventName)
        => new(TriggerKind.VsEvent, eventName);

    private static TriggerBinding BeforeCmd(string cmd)
        => new(TriggerKind.BeforeCommand, cmd);

    private static TriggerBinding AfterCmd(string cmd)
        => new(TriggerKind.AfterCommand, cmd);

    // ─── 1. Three failures → auto-disabled → filtered from FindByEvent ─────────────────

    [Fact]
    public async Task ThreeFailures_AutoDisabled_FilteredFrom_FindByEvent()
    {
        var store = new FakeStore();
        var entry = MakeEntry("OnBuild", VsEvent("Build.SolutionBuildDone"));
        store.Add(entry);

        var tracker = new MacroFailureTracker(threshold: 3);
        using var registry = new MacroTriggerRegistry(store, CreateJtf(), failureTracker: tracker);
        await registry.RefreshAsync();

        // Before any failures: macro is visible.
        Assert.Single(registry.FindByEvent("Build.SolutionBuildDone"));

        // Simulate 3 consecutive failures.
        tracker.RecordFailure(entry.Path);
        tracker.RecordFailure(entry.Path);
        tracker.RecordFailure(entry.Path);

        Assert.True(tracker.IsAutoDisabled(entry.Path));
        Assert.Empty(registry.FindByEvent("Build.SolutionBuildDone"));
    }

    [Fact]
    public async Task ThreeFailures_AutoDisabled_FilteredFrom_FindByBeforeCommand()
    {
        var store = new FakeStore();
        var entry = MakeEntry("BeforeSave", BeforeCmd("File.Save"));
        store.Add(entry);

        var tracker = new MacroFailureTracker(threshold: 3);
        using var registry = new MacroTriggerRegistry(store, CreateJtf(), failureTracker: tracker);
        await registry.RefreshAsync();

        Assert.Single(registry.FindByBeforeCommand("File.Save"));

        tracker.RecordFailure(entry.Path);
        tracker.RecordFailure(entry.Path);
        tracker.RecordFailure(entry.Path);

        Assert.True(tracker.IsAutoDisabled(entry.Path));
        Assert.Empty(registry.FindByBeforeCommand("File.Save"));
    }

    [Fact]
    public async Task ThreeFailures_AutoDisabled_FilteredFrom_FindByAfterCommand()
    {
        var store = new FakeStore();
        var entry = MakeEntry("AfterBuild", AfterCmd("Build.BuildSolution"));
        store.Add(entry);

        var tracker = new MacroFailureTracker(threshold: 3);
        using var registry = new MacroTriggerRegistry(store, CreateJtf(), failureTracker: tracker);
        await registry.RefreshAsync();

        Assert.Single(registry.FindByAfterCommand("Build.BuildSolution"));

        tracker.RecordFailure(entry.Path);
        tracker.RecordFailure(entry.Path);
        tracker.RecordFailure(entry.Path);

        Assert.True(tracker.IsAutoDisabled(entry.Path));
        Assert.Empty(registry.FindByAfterCommand("Build.BuildSolution"));
    }

    // ─── 2. Re-enable → registry returns it again ────────────────────────────────────

    [Fact]
    public async Task ReEnable_After_AutoDisable_Restores_FindByEvent_Visibility()
    {
        var store = new FakeStore();
        var entry = MakeEntry("OnBuild", VsEvent("Build.SolutionBuildDone"));
        store.Add(entry);

        var tracker = new MacroFailureTracker(threshold: 3);
        using var registry = new MacroTriggerRegistry(store, CreateJtf(), failureTracker: tracker);
        await registry.RefreshAsync();

        // Auto-disable.
        tracker.RecordFailure(entry.Path);
        tracker.RecordFailure(entry.Path);
        tracker.RecordFailure(entry.Path);
        Assert.Empty(registry.FindByEvent("Build.SolutionBuildDone"));

        // Re-enable.
        tracker.ReEnable(entry.Path);

        Assert.False(tracker.IsAutoDisabled(entry.Path));
        Assert.Single(registry.FindByEvent("Build.SolutionBuildDone"));
    }

    [Fact]
    public async Task ReEnable_Allows_Fresh_Failure_Streak_To_Start()
    {
        var store = new FakeStore();
        var entry = MakeEntry("OnBuild", VsEvent("Build.SolutionBuildDone"));
        store.Add(entry);

        var tracker = new MacroFailureTracker(threshold: 3);
        using var registry = new MacroTriggerRegistry(store, CreateJtf(), failureTracker: tracker);
        await registry.RefreshAsync();

        // First auto-disable cycle.
        tracker.RecordFailure(entry.Path);
        tracker.RecordFailure(entry.Path);
        tracker.RecordFailure(entry.Path);
        Assert.True(tracker.IsAutoDisabled(entry.Path));

        // Re-enable.
        tracker.ReEnable(entry.Path);
        Assert.False(tracker.IsAutoDisabled(entry.Path));

        // Two more failures — should NOT re-disable yet.
        tracker.RecordFailure(entry.Path);
        tracker.RecordFailure(entry.Path);
        Assert.False(tracker.IsAutoDisabled(entry.Path));
        Assert.Single(registry.FindByEvent("Build.SolutionBuildDone"));

        // Third failure — re-disables.
        tracker.RecordFailure(entry.Path);
        Assert.True(tracker.IsAutoDisabled(entry.Path));
        Assert.Empty(registry.FindByEvent("Build.SolutionBuildDone"));
    }

    // ─── 3. Successful execution after 2 failures → resets streak ─────────────────────

    [Fact]
    public async Task TwoFailures_ThenSuccess_Resets_Streak_Not_Disabled()
    {
        var store = new FakeStore();
        var entry = MakeEntry("OnBuild", VsEvent("Build.SolutionBuildDone"));
        store.Add(entry);

        var tracker = new MacroFailureTracker(threshold: 3);
        using var registry = new MacroTriggerRegistry(store, CreateJtf(), failureTracker: tracker);
        await registry.RefreshAsync();

        // Two failures — below threshold.
        tracker.RecordFailure(entry.Path);
        tracker.RecordFailure(entry.Path);
        Assert.False(tracker.IsAutoDisabled(entry.Path));
        Assert.Single(registry.FindByEvent("Build.SolutionBuildDone"));

        // Success resets the streak.
        tracker.RecordSuccess(entry.Path);
        Assert.False(tracker.IsAutoDisabled(entry.Path));

        // Now one more failure — streak should restart from 1, not reach threshold.
        tracker.RecordFailure(entry.Path);
        Assert.False(tracker.IsAutoDisabled(entry.Path));
        Assert.Single(registry.FindByEvent("Build.SolutionBuildDone"));
    }

    [Fact]
    public async Task TwoFailures_ThenSuccess_Two_More_Failures_Still_Below_Threshold()
    {
        var store = new FakeStore();
        var entry = MakeEntry("OnBuild", VsEvent("Build.SolutionBuildDone"));
        store.Add(entry);

        var tracker = new MacroFailureTracker(threshold: 3);
        using var registry = new MacroTriggerRegistry(store, CreateJtf(), failureTracker: tracker);
        await registry.RefreshAsync();

        tracker.RecordFailure(entry.Path);
        tracker.RecordFailure(entry.Path);
        tracker.RecordSuccess(entry.Path); // resets streak
        tracker.RecordFailure(entry.Path);
        tracker.RecordFailure(entry.Path);

        // Still 2 failures in new streak — not disabled.
        Assert.False(tracker.IsAutoDisabled(entry.Path));
        Assert.Single(registry.FindByEvent("Build.SolutionBuildDone"));
    }

    // ─── 4. Multiple macros — one disabled does not affect others ────────────────────

    [Fact]
    public async Task OneOfTwo_Disabled_Other_Remains_Visible()
    {
        var store = new FakeStore();
        var entryA = MakeEntry("MacroA", VsEvent("Build.SolutionBuildDone"));
        var entryB = MakeEntry("MacroB", VsEvent("Build.SolutionBuildDone"));
        store.Add(entryA);
        store.Add(entryB);

        var tracker = new MacroFailureTracker(threshold: 3);
        using var registry = new MacroTriggerRegistry(store, CreateJtf(), failureTracker: tracker);
        await registry.RefreshAsync();

        Assert.Equal(2, registry.FindByEvent("Build.SolutionBuildDone").Count);

        // Disable only A.
        tracker.RecordFailure(entryA.Path);
        tracker.RecordFailure(entryA.Path);
        tracker.RecordFailure(entryA.Path);

        var matches = registry.FindByEvent("Build.SolutionBuildDone");
        Assert.Single(matches);
        Assert.Equal("MacroB", matches[0].Entry.Name);
    }

    [Fact]
    public async Task AutoDisabled_Event_Fires_On_Third_Failure()
    {
        var store = new FakeStore();
        var entry = MakeEntry("OnBuild", VsEvent("Build.SolutionBuildDone"));
        store.Add(entry);

        var tracker = new MacroFailureTracker(threshold: 3);
        using var registry = new MacroTriggerRegistry(store, CreateJtf(), failureTracker: tracker);
        await registry.RefreshAsync();

        MacroAutoDisabledEventArgs? captured = null;
        tracker.AutoDisabled += (_, e) => captured = e;

        tracker.RecordFailure(entry.Path);
        tracker.RecordFailure(entry.Path);
        Assert.Null(captured); // not yet

        tracker.RecordFailure(entry.Path);
        Assert.NotNull(captured);
        Assert.Equal(entry.Path, captured!.MacroPath);
        Assert.Equal("OnBuild", captured.MacroName);
    }

    // ─── 5. Tracker without registry — standalone verification ────────────────────────

    [Fact]
    public void Tracker_GetDisabledPaths_Returns_AllDisabled()
    {
        var tracker = new MacroFailureTracker(threshold: 2);

        tracker.RecordFailure(@"X:\a.csx");
        tracker.RecordFailure(@"X:\a.csx");
        tracker.RecordFailure(@"X:\b.csx");
        tracker.RecordFailure(@"X:\b.csx");

        var disabled = tracker.GetDisabledPaths();

        Assert.Equal(2, disabled.Count);
        Assert.Contains(@"X:\a.csx", disabled, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(@"X:\b.csx", disabled, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tracker_RecordSuccess_Removes_From_GetDisabledPaths()
    {
        var tracker = new MacroFailureTracker(threshold: 2);
        tracker.RecordFailure(@"X:\a.csx");
        tracker.RecordFailure(@"X:\a.csx");
        Assert.True(tracker.IsAutoDisabled(@"X:\a.csx"));

        tracker.RecordSuccess(@"X:\a.csx");

        Assert.False(tracker.IsAutoDisabled(@"X:\a.csx"));
        Assert.Empty(tracker.GetDisabledPaths());
    }

    // ─── Fake store ───────────────────────────────────────────────────────────────────

    private sealed class FakeStore : IMacroStore
    {
        private readonly List<MacroEntry> _entries = new();

        public void Add(MacroEntry entry) => _entries.Add(entry);

        public event EventHandler<MacroLibraryChangedEventArgs>? LibraryChanged
        {
            add { }
            remove { }
        }

        public string CurrentPath => @"X:\fake\current.csx";

        public Task SaveCurrentAsync(string source, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> LoadCurrentAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<bool> DeleteCurrentAsync(CancellationToken ct = default) => Task.FromResult(false);

        public Task<IReadOnlyList<MacroEntry>> ListAsync(MacroScope scope, CancellationToken ct = default)
        {
            IReadOnlyList<MacroEntry> result = _entries.Where(e => e.Scope == scope).ToArray();
            return Task.FromResult(result);
        }

        public async Task<IReadOnlyList<MacroEntry>> ListAllAsync(CancellationToken ct = default)
        {
            await Task.Yield();
            return _entries.ToArray();
        }

        public Task<MacroEntry?> RefreshEntryAsync(string name, MacroScope scope, CancellationToken ct = default)
            => Task.FromResult<MacroEntry?>(null);

        public Task<string?> LoadByNameAsync(string name, MacroScope scope, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task SaveAsAsync(string name, string source, MacroScope scope, bool overwrite = false, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> DeleteAsync(string name, MacroScope scope, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task RenameAsync(string oldName, string newName, MacroScope scope, CancellationToken ct = default)
            => Task.CompletedTask;

        public string GetMacroPath(string name, MacroScope scope) => $@"X:\fake\{scope}\{name}.csx";
        public bool IsValidName(string name) => !string.IsNullOrWhiteSpace(name);
    }
}
