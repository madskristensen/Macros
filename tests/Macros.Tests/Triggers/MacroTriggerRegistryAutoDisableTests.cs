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
/// Verifies that <see cref="MacroTriggerRegistry"/> correctly filters out auto-disabled macros
/// in all <c>FindByXxx</c> lookups, and that re-enabling restores visibility.
/// </summary>
public sealed class MacroTriggerRegistryAutoDisableTests
{
#pragma warning disable VSSDK005
    private static JoinableTaskFactory CreateJtf()
        => new JoinableTaskContext().Factory;
#pragma warning restore VSSDK005

    private static MacroEntry MakeEntry(string name, params TriggerBinding[] triggers)
        => new(
            name,
            MacroScope.Global,
            $@"X:\fake\{name}.csx",
            StepCount: 0,
            Modified: DateTimeOffset.UtcNow,
            SizeBytes: 0,
            Triggers: triggers.Length == 0
                ? new[] { TriggerBinding.Manual }
                : triggers);

    [Fact]
    public async Task AutoDisabled_Macro_ExcludedFrom_FindByEvent()
    {
        var store = new RegistryAutoDisableFakeStore();
        var entry = MakeEntry(
            "OnBuild",
            new TriggerBinding(TriggerKind.VsEvent, "Build.SolutionBuildDone"));
        store.Entries.Add(entry);

        var tracker = new MacroFailureTracker(threshold: 3);
        using var registry = new MacroTriggerRegistry(store, CreateJtf(), failureTracker: tracker);
        await registry.RefreshAsync();

        // Sanity: macro visible before disabling.
        Assert.Single(registry.FindByEvent("Build.SolutionBuildDone"));

        // Trigger auto-disable.
        tracker.RecordFailure(entry.Path);
        tracker.RecordFailure(entry.Path);
        tracker.RecordFailure(entry.Path);

        Assert.True(tracker.IsAutoDisabled(entry.Path));
        Assert.Empty(registry.FindByEvent("Build.SolutionBuildDone"));
    }

    [Fact]
    public async Task AutoDisabled_Macro_ExcludedFrom_FindByBeforeCommand()
    {
        var store = new RegistryAutoDisableFakeStore();
        var entry = MakeEntry(
            "BeforeSave",
            new TriggerBinding(TriggerKind.BeforeCommand, "File.Save"));
        store.Entries.Add(entry);

        var tracker = new MacroFailureTracker(threshold: 3);
        using var registry = new MacroTriggerRegistry(store, CreateJtf(), failureTracker: tracker);
        await registry.RefreshAsync();

        Assert.Single(registry.FindByBeforeCommand("File.Save"));

        tracker.RecordFailure(entry.Path);
        tracker.RecordFailure(entry.Path);
        tracker.RecordFailure(entry.Path);

        Assert.Empty(registry.FindByBeforeCommand("File.Save"));
    }

    [Fact]
    public async Task AutoDisabled_Macro_ExcludedFrom_FindByAfterCommand()
    {
        var store = new RegistryAutoDisableFakeStore();
        var entry = MakeEntry(
            "AfterBuild",
            new TriggerBinding(TriggerKind.AfterCommand, "Build.BuildSolution"));
        store.Entries.Add(entry);

        var tracker = new MacroFailureTracker(threshold: 3);
        using var registry = new MacroTriggerRegistry(store, CreateJtf(), failureTracker: tracker);
        await registry.RefreshAsync();

        Assert.Single(registry.FindByAfterCommand("Build.BuildSolution"));

        tracker.RecordFailure(entry.Path);
        tracker.RecordFailure(entry.Path);
        tracker.RecordFailure(entry.Path);

        Assert.Empty(registry.FindByAfterCommand("Build.BuildSolution"));
    }

    [Fact]
    public async Task ReEnable_Restores_FindByEvent_Visibility()
    {
        var store = new RegistryAutoDisableFakeStore();
        var entry = MakeEntry(
            "OnBuild",
            new TriggerBinding(TriggerKind.VsEvent, "Build.SolutionBuildDone"));
        store.Entries.Add(entry);

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
        Assert.Single(registry.FindByEvent("Build.SolutionBuildDone"));
    }

    [Fact]
    public async Task NonDisabled_Macro_Unaffected_When_Other_IsDisabled()
    {
        var store = new RegistryAutoDisableFakeStore();
        var entry1 = MakeEntry("A", new TriggerBinding(TriggerKind.VsEvent, "Build.SolutionBuildDone"));
        var entry2 = MakeEntry("B", new TriggerBinding(TriggerKind.VsEvent, "Build.SolutionBuildDone"));
        store.Entries.Add(entry1);
        store.Entries.Add(entry2);

        var tracker = new MacroFailureTracker(threshold: 3);
        using var registry = new MacroTriggerRegistry(store, CreateJtf(), failureTracker: tracker);
        await registry.RefreshAsync();

        Assert.Equal(2, registry.FindByEvent("Build.SolutionBuildDone").Count);

        // Disable only entry1.
        tracker.RecordFailure(entry1.Path);
        tracker.RecordFailure(entry1.Path);
        tracker.RecordFailure(entry1.Path);

        var matches = registry.FindByEvent("Build.SolutionBuildDone");
        Assert.Single(matches);
        Assert.Equal("B", matches[0].Entry.Name);
    }

    /// <summary>Minimal fake store for auto-disable registry tests.</summary>
    private sealed class RegistryAutoDisableFakeStore : IMacroStore
    {
        public List<MacroEntry> Entries { get; } = new();

        public event EventHandler<MacroLibraryChangedEventArgs>? LibraryChanged
        {
            add { }
            remove { }
        }

        public string CurrentPath => @"X:\fake\current.csx";
        public Task SaveCurrentAsync(string source, CancellationToken cancellation = default) => Task.CompletedTask;
        public Task<string?> LoadCurrentAsync(CancellationToken cancellation = default) => Task.FromResult<string?>(null);
        public Task<bool> DeleteCurrentAsync(CancellationToken cancellation = default) => Task.FromResult(false);
        public Task<IReadOnlyList<MacroEntry>> ListAsync(MacroScope scope, CancellationToken cancellation = default)
        {
            IReadOnlyList<MacroEntry> result = Entries.Where(e => e.Scope == scope).ToArray();
            return Task.FromResult(result);
        }
        public async Task<IReadOnlyList<MacroEntry>> ListAllAsync(CancellationToken cancellation = default)
        {
            await Task.Yield();
            return Entries.ToArray();
        }
        public Task<MacroEntry?> RefreshEntryAsync(string name, MacroScope scope, CancellationToken cancellation = default) => Task.FromResult<MacroEntry?>(null);
        public Task<string?> LoadByNameAsync(string name, MacroScope scope, CancellationToken cancellation = default) => Task.FromResult<string?>(null);
        public Task SaveAsAsync(string name, string source, MacroScope scope, bool overwrite = false, CancellationToken cancellation = default) => Task.CompletedTask;
        public Task<bool> DeleteAsync(string name, MacroScope scope, CancellationToken cancellation = default) => Task.FromResult(false);
        public Task RenameAsync(string oldName, string newName, MacroScope scope, CancellationToken cancellation = default) => Task.CompletedTask;
        public string GetMacroPath(string name, MacroScope scope) => $@"X:\fake\{scope}\{name}.csx";
        public bool IsValidName(string name) => !string.IsNullOrWhiteSpace(name);
    }
}
