using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine.Storage;
using Macros.Engine.Triggers;
using Macros.ToolWindows;
using Xunit;

namespace Macros.Tests.ToolWindows;

/// <summary>
/// M4 grouped-list-view + shadowing tests for <see cref="MacrosToolWindowViewModel"/>.
/// Verifies that the new <see cref="MacrosToolWindowViewModel.GroupedItemsView"/> projection
/// correctly partitions rows into Repo / Global / Shadowed-Global, and that shadowing is
/// detected by name-collision with a same-name repo macro.
/// </summary>
public sealed class MacrosToolWindowViewModelGroupedTests
{
    private const string FakeRoot = "X:\\fake";

    [Fact]
    public async Task EmptyStores_GroupedItemsView_IsEmpty()
    {
        var storage = new FakeStorage(repoAvailable: true);

        using var vm = new MacrosToolWindowViewModel(storage, debounceInterval: TimeSpan.Zero);
        await vm.LoadAsync();

        Assert.Equal(15, vm.AllItems.Count);
        Assert.All(vm.AllItems, item => Assert.True(item.IsSample));
        Assert.Equal(new[] { "Samples" }, ItemGroupNames(vm));
        Assert.Equal(15, vm.GroupedItemsView.Cast<object>().Count());
        Assert.True(vm.IsEmpty);
        var shadowedGroup = vm.Groups.Single(g => g.IsShadowed);
        Assert.False(shadowedGroup.IsAvailable);
    }

    [Fact]
    public async Task GlobalA_RepoA_ShadowingDetected_AppearsInBothBuckets()
    {
        var storage = new FakeStorage(repoAvailable: true);
        storage.Add(MacroScope.Global, "A");
        storage.Add(MacroScope.Repo, "A");

        using var vm = new MacrosToolWindowViewModel(storage, debounceInterval: TimeSpan.Zero);
        await vm.LoadAsync();

        var repoGroup = vm.Groups.Single(g => g.Scope == MacroScope.Repo);
        var globalGroup = vm.Groups.Single(g => g.Scope == MacroScope.Global && !g.IsShadowed);
        var shadowedGroup = vm.Groups.Single(g => g.IsShadowed);

        // Repo wins normal display.
        Assert.Single(repoGroup.Items);
        Assert.Equal("A", repoGroup.Items[0].Name);
        Assert.False(repoGroup.Items[0].IsShadowed);
        Assert.Equal("Repo", repoGroup.Items[0].GroupName);

        // Non-shadowed global section is empty.
        Assert.Empty(globalGroup.Items);

        // Shadowed-global section surfaces the global "loser".
        Assert.True(shadowedGroup.IsAvailable);
        Assert.Single(shadowedGroup.Items);
        Assert.Equal("A", shadowedGroup.Items[0].Name);
        Assert.True(shadowedGroup.Items[0].IsShadowed);
        Assert.Equal("Shadowed Global Macros", shadowedGroup.Items[0].GroupName);

        // The flat view-model collection contains the repo "A", the shadowed "A", and the sample rows.
        var names = vm.AllItems.Select(i => (i.Name, i.GroupName)).ToList();
        Assert.Contains(("A", "Repo"), names);
        Assert.Contains(("A", "Shadowed Global Macros"), names);
        Assert.Equal(17, names.Count);
        Assert.Equal(15, names.Count(pair => pair.GroupName == "Samples"));

        // GroupedItemsView surfaces Repo, Shadowed Global, and Samples.
        var groupNames = ItemGroupNames(vm).ToList();
        Assert.Contains("Repo", groupNames);
        Assert.Contains("Shadowed Global Macros", groupNames);
        Assert.Contains("Samples", groupNames);
        Assert.DoesNotContain("Global", groupNames);
    }

    [Fact]
    public async Task GlobalAB_RepoB_OnlyBIsShadowed()
    {
        var storage = new FakeStorage(repoAvailable: true);
        storage.Add(MacroScope.Global, "A");
        storage.Add(MacroScope.Global, "B");
        storage.Add(MacroScope.Repo, "B");

        using var vm = new MacrosToolWindowViewModel(storage, debounceInterval: TimeSpan.Zero);
        await vm.LoadAsync();

        var repoGroup = vm.Groups.Single(g => g.Scope == MacroScope.Repo);
        var globalGroup = vm.Groups.Single(g => g.Scope == MacroScope.Global && !g.IsShadowed);
        var shadowedGroup = vm.Groups.Single(g => g.IsShadowed);

        Assert.Equal(new[] { "B" }, repoGroup.Items.Select(i => i.Name));
        Assert.Equal(new[] { "A" }, globalGroup.Items.Select(i => i.Name));
        Assert.Equal(new[] { "B" }, shadowedGroup.Items.Select(i => i.Name));

        Assert.True(globalGroup.Items[0].GroupName == "Global");
        Assert.False(globalGroup.Items[0].IsShadowed);
        Assert.True(shadowedGroup.Items[0].IsShadowed);

        // Three sections visible.
        Assert.True(repoGroup.IsAvailable);
        Assert.True(globalGroup.IsAvailable);
        Assert.True(shadowedGroup.IsAvailable);

        var groupNames = ItemGroupNames(vm).ToList();
        Assert.Contains("Repo", groupNames);
        Assert.Contains("Global", groupNames);
        Assert.Contains("Shadowed Global Macros", groupNames);
        Assert.Contains("Samples", groupNames);
    }

    [Fact]
    public async Task ShadowingComparison_IsCaseInsensitive()
    {
        var storage = new FakeStorage(repoAvailable: true);
        storage.Add(MacroScope.Global, "Greeting");
        storage.Add(MacroScope.Repo, "GREETING");

        using var vm = new MacrosToolWindowViewModel(storage, debounceInterval: TimeSpan.Zero);
        await vm.LoadAsync();

        var shadowedGroup = vm.Groups.Single(g => g.IsShadowed);
        Assert.Single(shadowedGroup.Items);
        Assert.Equal("Greeting", shadowedGroup.Items[0].Name);
    }

    [Fact]
    public async Task NoRepoAvailable_NoShadowing_AllGlobalsRenderNormally()
    {
        var storage = new FakeStorage(repoAvailable: false);
        storage.Add(MacroScope.Global, "Solo");

        using var vm = new MacrosToolWindowViewModel(storage, debounceInterval: TimeSpan.Zero);
        await vm.LoadAsync();

        var repoGroup = vm.Groups.Single(g => g.Scope == MacroScope.Repo);
        var globalGroup = vm.Groups.Single(g => g.Scope == MacroScope.Global && !g.IsShadowed);
        var shadowedGroup = vm.Groups.Single(g => g.IsShadowed);

        Assert.False(repoGroup.IsAvailable);
        Assert.False(shadowedGroup.IsAvailable);
        Assert.Single(globalGroup.Items);
    }

    private static IEnumerable<string> ItemGroupNames(MacrosToolWindowViewModel vm)
        => vm.AllItems.Select(i => i.GroupName).Distinct();

    /// <summary>
    /// Hand-rolled <see cref="IMacroStore"/> double — separately tracks Global and Repo
    /// entries so the VM's per-scope <c>ListAsync</c> calls can reproduce shadowing.
    /// </summary>
    private sealed class FakeStorage : IMacroStore
    {
        private readonly Dictionary<(MacroScope, string), MacroEntry> _files = new();
        private readonly bool _repoAvailable;

        public FakeStorage(bool repoAvailable)
        {
            _repoAvailable = repoAvailable;
        }

        public string CurrentPath => $"{FakeRoot}\\current.csx";
        public event EventHandler<MacroLibraryChangedEventArgs>? LibraryChanged;

        public void Add(MacroScope scope, string name)
        {
            _files[(scope, name)] = new MacroEntry(
                name,
                scope,
                $"{FakeRoot}\\{scope}\\{name}.csx",
                0,
                DateTimeOffset.UtcNow,
                128,
                Array.Empty<TriggerBinding>());
        }

        public Task SaveCurrentAsync(string source, CancellationToken cancellation = default) => Task.CompletedTask;
        public Task<string?> LoadCurrentAsync(CancellationToken cancellation = default) => Task.FromResult<string?>(null);
        public Task<bool> DeleteCurrentAsync(CancellationToken cancellation = default) => Task.FromResult(false);

        public Task<IReadOnlyList<MacroEntry>> ListAsync(MacroScope scope, CancellationToken cancellation = default)
        {
            if (scope == MacroScope.Repo && !_repoAvailable)
            {
                throw new InvalidOperationException("No solution.");
            }

            var list = _files
                .Where(kv => kv.Key.Item1 == scope)
                .Select(kv => kv.Value)
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return Task.FromResult<IReadOnlyList<MacroEntry>>(list);
        }

        public Task<IReadOnlyList<MacroEntry>> ListAllAsync(CancellationToken cancellation = default)
            => throw new NotSupportedException("M4 VM uses ListAsync per scope.");

        public Task<string?> LoadByNameAsync(string name, MacroScope scope, CancellationToken cancellation = default)
            => Task.FromResult<string?>(null);

        public Task SaveAsAsync(string name, string source, MacroScope scope, bool overwrite = false, CancellationToken cancellation = default)
            => Task.CompletedTask;

        public Task<bool> DeleteAsync(string name, MacroScope scope, CancellationToken cancellation = default)
            => Task.FromResult(false);

        public Task RenameAsync(string oldName, string newName, MacroScope scope, CancellationToken cancellation = default)
            => Task.CompletedTask;

        public string GetMacroPath(string name, MacroScope scope) => $"{FakeRoot}\\{scope}\\{name}.csx";

        public bool IsValidName(string name) => !string.IsNullOrWhiteSpace(name);

        public Task<MacroEntry?> RefreshEntryAsync(string name, MacroScope scope, CancellationToken cancellation = default)
        {
            _files.TryGetValue((scope, name), out var entry);
            return Task.FromResult<MacroEntry?>(entry);
        }

        // Suppress "event never used" — kept on the interface contract.
        internal void RaiseChangedForTests() => LibraryChanged?.Invoke(this, new MacroLibraryChangedEventArgs(MacroLibraryChangeKind.Added, MacroScope.Global, "x"));
    }
}
