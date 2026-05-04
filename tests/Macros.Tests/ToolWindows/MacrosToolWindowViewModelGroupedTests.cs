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
/// Grouped-list-view tests for <see cref="MacrosToolWindowViewModel"/>.
/// Verifies that the <see cref="MacrosToolWindowViewModel.GroupedItemsView"/> projection
/// correctly partitions rows into Repo / Global / Samples groups.
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
    }

    [Fact]
    public async Task GlobalA_RepoA_BothVisible()
    {
        var storage = new FakeStorage(repoAvailable: true);
        storage.Add(MacroScope.Global, "A");
        storage.Add(MacroScope.Repo, "A");

        using var vm = new MacrosToolWindowViewModel(storage, debounceInterval: TimeSpan.Zero);
        await vm.LoadAsync();

        var repoGroup = vm.Groups.Single(g => g.Scope == MacroScope.Repo);
        var globalGroup = vm.Groups.Single(g => g.Scope == MacroScope.Global);

        // Repo contains A
        Assert.Single(repoGroup.Items);
        Assert.Equal("A", repoGroup.Items[0].Name);
        Assert.Equal("Repo", repoGroup.Items[0].GroupName);

        // Global also contains A
        Assert.Single(globalGroup.Items);
        Assert.Equal("A", globalGroup.Items[0].Name);
        Assert.Equal("Global", globalGroup.Items[0].GroupName);

        // The flat view-model collection contains both A entries and the sample rows.
        var names = vm.AllItems.Select(i => (i.Name, i.GroupName)).ToList();
        Assert.Contains(("A", "Repo"), names);
        Assert.Contains(("A", "Global"), names);
        Assert.Equal(17, names.Count);
        Assert.Equal(15, names.Count(pair => pair.GroupName == "Samples"));

        // GroupedItemsView surfaces Repo, Global, and Samples.
        var groupNames = ItemGroupNames(vm).ToList();
        Assert.Contains("Repo", groupNames);
        Assert.Contains("Global", groupNames);
        Assert.Contains("Samples", groupNames);
    }

    [Fact]
    public async Task GlobalAB_RepoB_AllVisible()
    {
        var storage = new FakeStorage(repoAvailable: true);
        storage.Add(MacroScope.Global, "A");
        storage.Add(MacroScope.Global, "B");
        storage.Add(MacroScope.Repo, "B");

        using var vm = new MacrosToolWindowViewModel(storage, debounceInterval: TimeSpan.Zero);
        await vm.LoadAsync();

        var repoGroup = vm.Groups.Single(g => g.Scope == MacroScope.Repo);
        var globalGroup = vm.Groups.Single(g => g.Scope == MacroScope.Global);

        Assert.Equal(new[] { "B" }, repoGroup.Items.Select(i => i.Name));
        Assert.Equal(new[] { "A", "B" }, globalGroup.Items.Select(i => i.Name));

        Assert.Equal("Repo", repoGroup.Items[0].GroupName);
        Assert.Equal("Global", globalGroup.Items[0].GroupName);
        Assert.Equal("Global", globalGroup.Items[1].GroupName);

        // Both sections visible.
        Assert.True(repoGroup.IsAvailable);
        Assert.True(globalGroup.IsAvailable);

        var groupNames = ItemGroupNames(vm).ToList();
        Assert.Contains("Repo", groupNames);
        Assert.Contains("Global", groupNames);
        Assert.Contains("Samples", groupNames);
    }

    [Fact]
    public async Task NameComparison_IsCaseInsensitive()
    {
        var storage = new FakeStorage(repoAvailable: true);
        storage.Add(MacroScope.Global, "Greeting");
        storage.Add(MacroScope.Repo, "GREETING");

        using var vm = new MacrosToolWindowViewModel(storage, debounceInterval: TimeSpan.Zero);
        await vm.LoadAsync();

        var repoGroup = vm.Groups.Single(g => g.Scope == MacroScope.Repo);
        var globalGroup = vm.Groups.Single(g => g.Scope == MacroScope.Global);

        Assert.Single(repoGroup.Items);
        Assert.Equal("GREETING", repoGroup.Items[0].Name);
        Assert.Single(globalGroup.Items);
        Assert.Equal("Greeting", globalGroup.Items[0].Name);
    }

    [Fact]
    public async Task NoRepoAvailable_AllGlobalsRenderNormally()
    {
        var storage = new FakeStorage(repoAvailable: false);
        storage.Add(MacroScope.Global, "Solo");

        using var vm = new MacrosToolWindowViewModel(storage, debounceInterval: TimeSpan.Zero);
        await vm.LoadAsync();

        var repoGroup = vm.Groups.Single(g => g.Scope == MacroScope.Repo);
        var globalGroup = vm.Groups.Single(g => g.Scope == MacroScope.Global);

        Assert.False(repoGroup.IsAvailable);
        Assert.Single(globalGroup.Items);
    }

    private static IEnumerable<string> ItemGroupNames(MacrosToolWindowViewModel vm)
        => vm.AllItems.Select(i => i.GroupName).Distinct();

    /// <summary>
    /// Hand-rolled <see cref="IMacroStore"/> double — separately tracks Global and Repo
    /// entries.
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
