using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Macros.Engine.Storage;
using Macros.Engine.Triggers;
using Microsoft.VisualStudio.Threading;
using Xunit;

namespace Macros.Tests.Triggers;

/// <summary>
/// Pins the contract of the user-disabled provider hook on
/// <see cref="MacroTriggerRegistry"/> (issue #8). The hook is read live on every
/// lookup so disabling a macro suppresses its triggers immediately, without an
/// index rebuild — and re-enabling it restores visibility just as fast.
/// </summary>
public sealed class MacroTriggerRegistryUserDisabledTests
{
    private static JoinableTaskFactory CreateJtf()
    {
#pragma warning disable VSSDK005 // Use ThreadHelper.JoinableTaskContext — not available in unit tests.
        return new JoinableTaskContext().Factory;
#pragma warning restore VSSDK005
    }

    private static MacroEntry MakeEntry(string name, params TriggerBinding[] triggers)
        => new(
            name,
            MacroScope.Global,
            $@"X:\fake\{name}.csx",
            StepCount: 0,
            Modified: DateTimeOffset.UtcNow,
            SizeBytes: 0,
            Triggers: triggers.Length == 0 ? new[] { TriggerBinding.Manual } : triggers);

    [Fact]
    public async Task UserDisabled_FilteredFrom_FindByEvent()
    {
        var store = new FakeStore();
        var entry = MakeEntry("OnBuild", new TriggerBinding(TriggerKind.VsEvent, "Build.SolutionBuildDone"));
        store.Entries.Add(entry);

        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var registry = new MacroTriggerRegistry(
            store,
            CreateJtf(),
            isUserDisabledProvider: path => disabled.Contains(path));

        await registry.RefreshAsync();

        // Initially enabled — the macro is visible.
        Assert.Single(registry.FindByEvent("Build.SolutionBuildDone"));

        // Disabling flips the hook live: no rebuild, lookup short-circuits.
        disabled.Add(entry.Path);
        Assert.Empty(registry.FindByEvent("Build.SolutionBuildDone"));

        // Re-enabling restores visibility just as fast.
        disabled.Remove(entry.Path);
        Assert.Single(registry.FindByEvent("Build.SolutionBuildDone"));
    }

    [Fact]
    public async Task UserDisabled_FilteredFrom_FindByBeforeCommand_And_AfterCommand()
    {
        var store = new FakeStore();
        var before = MakeEntry("BeforeSave", new TriggerBinding(TriggerKind.BeforeCommand, "File.Save"));
        var after = MakeEntry("AfterBuild", new TriggerBinding(TriggerKind.AfterCommand, "Build.BuildSolution"));
        store.Entries.Add(before);
        store.Entries.Add(after);

        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var registry = new MacroTriggerRegistry(
            store,
            CreateJtf(),
            isUserDisabledProvider: path => disabled.Contains(path));

        await registry.RefreshAsync();

        Assert.Single(registry.FindByBeforeCommand("File.Save"));
        Assert.Single(registry.FindByAfterCommand("Build.BuildSolution"));

        disabled.Add(before.Path);
        disabled.Add(after.Path);

        Assert.Empty(registry.FindByBeforeCommand("File.Save"));
        Assert.Empty(registry.FindByAfterCommand("Build.BuildSolution"));
    }

    [Fact]
    public async Task UserDisabled_OneOfTwo_Other_Remains_Visible()
    {
        var store = new FakeStore();
        var keep = MakeEntry("Keep", new TriggerBinding(TriggerKind.VsEvent, "Build.SolutionBuildDone"));
        var hide = MakeEntry("Hide", new TriggerBinding(TriggerKind.VsEvent, "Build.SolutionBuildDone"));
        store.Entries.Add(keep);
        store.Entries.Add(hide);

        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { hide.Path };
        using var registry = new MacroTriggerRegistry(
            store,
            CreateJtf(),
            isUserDisabledProvider: path => disabled.Contains(path));

        await registry.RefreshAsync();

        var matches = registry.FindByEvent("Build.SolutionBuildDone");
        Assert.Single(matches);
        Assert.Equal("Keep", matches[0].Entry.Name);
    }

    [Fact]
    public async Task UserDisabled_StacksWith_FailureTracker_Filtering()
    {
        var store = new FakeStore();
        var userBlocked = MakeEntry("U", new TriggerBinding(TriggerKind.VsEvent, "Build.SolutionBuildDone"));
        var autoBlocked = MakeEntry("A", new TriggerBinding(TriggerKind.VsEvent, "Build.SolutionBuildDone"));
        var visible = MakeEntry("V", new TriggerBinding(TriggerKind.VsEvent, "Build.SolutionBuildDone"));
        store.Entries.Add(userBlocked);
        store.Entries.Add(autoBlocked);
        store.Entries.Add(visible);

        var tracker = new MacroFailureTracker(threshold: 3);
        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { userBlocked.Path };

        using var registry = new MacroTriggerRegistry(
            store,
            CreateJtf(),
            failureTracker: tracker,
            isUserDisabledProvider: path => disabled.Contains(path));

        await registry.RefreshAsync();

        // Auto-disable the second macro via the failure-tracker path.
        tracker.RecordFailure(autoBlocked.Path);
        tracker.RecordFailure(autoBlocked.Path);
        tracker.RecordFailure(autoBlocked.Path);
        Assert.True(tracker.IsAutoDisabled(autoBlocked.Path));

        var matches = registry.FindByEvent("Build.SolutionBuildDone");
        Assert.Single(matches);
        Assert.Equal("V", matches[0].Entry.Name);
    }

    [Fact]
    public async Task UserDisabledProvider_Throwing_FailsClosed_StillReturnsResults()
    {
        // Contract: a throwing provider must not break dispatch. The registry treats it
        // as "not disabled" so manual operation continues unimpeded.
        var store = new FakeStore();
        var entry = MakeEntry("OnBuild", new TriggerBinding(TriggerKind.VsEvent, "Build.SolutionBuildDone"));
        store.Entries.Add(entry);

        using var registry = new MacroTriggerRegistry(
            store,
            CreateJtf(),
            isUserDisabledProvider: _ => throw new InvalidOperationException("boom"));

        await registry.RefreshAsync();

        Assert.Single(registry.FindByEvent("Build.SolutionBuildDone"));
    }

    [Fact]
    public async Task NoUserDisabledProvider_TreatsAllMacros_AsEnabled()
    {
        var store = new FakeStore();
        var entry = MakeEntry("OnBuild", new TriggerBinding(TriggerKind.VsEvent, "Build.SolutionBuildDone"));
        store.Entries.Add(entry);

        using var registry = new MacroTriggerRegistry(store, CreateJtf());
        await registry.RefreshAsync();

        Assert.Single(registry.FindByEvent("Build.SolutionBuildDone"));
    }

    /// <summary>
    /// Minimal in-memory <see cref="IMacroStore"/> double for these tests, mirroring
    /// the one in <c>MacroTriggerRegistryTests</c>.
    /// </summary>
    private sealed class FakeStore : IMacroStore
    {
        public List<MacroEntry> Entries { get; } = new();

        public event EventHandler<MacroLibraryChangedEventArgs>? LibraryChanged
        {
            add { } remove { }
        }

        public string CurrentPath => @"X:\fake\current.csx";

        public Task SaveCurrentAsync(string source, System.Threading.CancellationToken cancellation = default)
            => Task.CompletedTask;
        public Task<string?> LoadCurrentAsync(System.Threading.CancellationToken cancellation = default)
            => Task.FromResult<string?>(null);
        public Task<bool> DeleteCurrentAsync(System.Threading.CancellationToken cancellation = default)
            => Task.FromResult(false);

        public async Task<IReadOnlyList<MacroEntry>> ListAsync(MacroScope scope, System.Threading.CancellationToken cancellation = default)
        {
            await Task.Yield();
            return Entries.Where(e => e.Scope == scope).ToArray();
        }

        public async Task<IReadOnlyList<MacroEntry>> ListAllAsync(System.Threading.CancellationToken cancellation = default)
        {
            await Task.Yield();
            return Entries.ToArray();
        }

        public Task<MacroEntry?> RefreshEntryAsync(string name, MacroScope scope, System.Threading.CancellationToken cancellation = default)
            => Task.FromResult<MacroEntry?>(null);
        public Task<string?> LoadByNameAsync(string name, MacroScope scope, System.Threading.CancellationToken cancellation = default)
            => Task.FromResult<string?>(null);
        public Task SaveAsAsync(string name, string source, MacroScope scope, bool overwrite = false, System.Threading.CancellationToken cancellation = default)
            => Task.CompletedTask;
        public Task<bool> DeleteAsync(string name, MacroScope scope, System.Threading.CancellationToken cancellation = default)
            => Task.FromResult(false);
        public Task RenameAsync(string oldName, string newName, MacroScope scope, System.Threading.CancellationToken cancellation = default)
            => Task.CompletedTask;
        public string GetMacroPath(string name, MacroScope scope) => $@"X:\fake\{scope}\{name}.csx";
        public bool IsValidName(string name) => !string.IsNullOrWhiteSpace(name);
    }
}
