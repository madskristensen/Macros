using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine;
using Macros.Engine.Recording;
using Macros.Engine.Storage;
using Macros.ToolWindows;
using Xunit;

namespace Macros.Tests.ToolWindows;

/// <summary>
/// Verifies the M3 <see cref="MacrosToolWindowViewModel"/> behaviour outside of WPF —
/// load / partition / filter / dispose / event-driven reload / per-row gating. WPF binding
/// behaviour itself is exercised by the integration suite, not here.
/// </summary>
public sealed class MacrosToolWindowViewModelTests
{
    private const string FakeRoot = "X:\\fake";

    [Fact]
    public async Task LoadAsync_PartitionsDescriptorsByScope()
    {
        var storage = new FakeStorage(repoAvailable: true);
        storage.Add(MacroScope.Global, "Global-A");
        storage.Add(MacroScope.Global, "Global-B");
        storage.Add(MacroScope.Repo, "Repo-Only");

        using var vm = new MacrosToolWindowViewModel(storage, debounceInterval: TimeSpan.Zero);
        await vm.LoadAsync();

        Assert.Collection(
            vm.Groups,
            g =>
            {
                Assert.Equal(MacroScope.Repo, g.Scope);
                Assert.True(g.IsAvailable);
                Assert.Single(g.Items);
                Assert.Equal("Repo-Only", g.Items[0].Name);
            },
            g =>
            {
                Assert.Equal(MacroScope.Global, g.Scope);
                Assert.True(g.IsAvailable);
                Assert.Equal(new[] { "Global-A", "Global-B" }, g.Items.Select(i => i.Name));
            });

        Assert.False(vm.IsEmpty);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task LoadAsync_NoRepoSolution_HidesRepoGroup()
    {
        var storage = new FakeStorage(repoAvailable: false);
        storage.Add(MacroScope.Global, "Solo");

        using var vm = new MacrosToolWindowViewModel(storage, debounceInterval: TimeSpan.Zero);
        await vm.LoadAsync();

        var repo = vm.Groups.Single(g => g.Scope == MacroScope.Repo);
        Assert.False(repo.IsAvailable);
        Assert.False(repo.IsVisible);
    }

    [Fact]
    public async Task FilterText_FiltersBySubstring_CaseInsensitive()
    {
        var storage = new FakeStorage(repoAvailable: false);
        storage.Add(MacroScope.Global, "Format-On-Save");
        storage.Add(MacroScope.Global, "DeployScript");
        storage.Add(MacroScope.Global, "format-helper");

        using var vm = new MacrosToolWindowViewModel(storage, debounceInterval: TimeSpan.Zero);
        await vm.LoadAsync();

        vm.FilterText = "form";

        var global = vm.Groups.Single(g => g.Scope == MacroScope.Global);
        // VM sorts case-insensitively by Name; "format-helper" < "Format-On-Save" because
        // after the shared "format-" prefix 'h' (0x68) < 'O' (0x4F) under OrdinalIgnoreCase.
        Assert.Equal(
            new[] { "format-helper", "Format-On-Save" },
            global.VisibleItems.Select(i => i.Name));
        Assert.DoesNotContain("DeployScript", global.VisibleItems.Select(i => i.Name));
    }

    [Fact]
    public async Task FilterText_NoMatches_HidesGroup()
    {
        var storage = new FakeStorage(repoAvailable: false);
        storage.Add(MacroScope.Global, "OnlyOne");

        using var vm = new MacrosToolWindowViewModel(storage, debounceInterval: TimeSpan.Zero);
        await vm.LoadAsync();

        vm.FilterText = "zzz";

        var global = vm.Groups.Single(g => g.Scope == MacroScope.Global);
        Assert.Empty(global.VisibleItems);
        Assert.False(global.IsVisible);
        Assert.True(vm.IsEmpty);
    }

    [Fact]
    public async Task LibraryChanged_TriggersReload()
    {
        var storage = new FakeStorage(repoAvailable: false);
        storage.Add(MacroScope.Global, "First");

        using var vm = new MacrosToolWindowViewModel(storage, debounceInterval: TimeSpan.Zero);
        await vm.LoadAsync();
        int initial = vm.LoadCount;

        // Add a new macro and raise the event the storage normally fires.
        var nextLoad = vm.NextLoadAsync();
        storage.Add(MacroScope.Global, "Second");
        storage.RaiseChanged(MacroLibraryChangeKind.Added, MacroScope.Global, "Second");

        await Task.WhenAny(nextLoad, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.True(vm.LoadCount > initial);
        var global = vm.Groups.Single(g => g.Scope == MacroScope.Global);
        Assert.Equal(new[] { "First", "Second" }, global.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task Dispose_UnsubscribesLibraryChanged()
    {
        var storage = new FakeStorage(repoAvailable: false);
        var vm = new MacrosToolWindowViewModel(storage, debounceInterval: TimeSpan.Zero);
        await vm.LoadAsync();

        // Capture the invocation count via reflection on the backing field.
        int InvocationCount()
        {
            var field = typeof(FakeStorage).GetField(
                nameof(FakeStorage.LibraryChanged),
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            var del = field!.GetValue(storage) as Delegate;
            return del?.GetInvocationList().Length ?? 0;
        }

        int before = InvocationCount();
        Assert.True(before > 0, "VM should subscribe to LibraryChanged in its constructor");

        vm.Dispose();

        int after = InvocationCount();
        Assert.True(after < before, $"Dispose should detach the handler (before={before}, after={after})");
    }

    [Fact]
    public async Task ServiceStateChanged_FlipsCanInvokeOnAllItems()
    {
        var storage = new FakeStorage(repoAvailable: false);
        storage.Add(MacroScope.Global, "A");
        storage.Add(MacroScope.Global, "B");

        var service = new FakeMacroService();
        using var vm = new MacrosToolWindowViewModel(storage, service, debounceInterval: TimeSpan.Zero);
        await vm.LoadAsync();

        var global = vm.Groups.Single(g => g.Scope == MacroScope.Global);
        Assert.All(global.Items, item => Assert.True(item.CanInvoke));

        service.RaiseStateChanged(MacroState.Idle, MacroState.Playing);
        Assert.All(global.Items, item => Assert.False(item.CanInvoke));

        service.RaiseStateChanged(MacroState.Playing, MacroState.Idle);
        Assert.All(global.Items, item => Assert.True(item.CanInvoke));
    }

    [Fact]
    public async Task PlayCommand_OnItem_CallsServicePlayByName()
    {
        var storage = new FakeStorage(repoAvailable: false);
        storage.Add(MacroScope.Global, "Greeting");

        var service = new FakeMacroService();
        using var vm = new MacrosToolWindowViewModel(storage, service, debounceInterval: TimeSpan.Zero);
        await vm.LoadAsync();

        var item = vm.Groups.Single(g => g.Scope == MacroScope.Global).Items.Single();
        Assert.True(item.PlayCommand.CanExecute(null));

        item.PlayCommand.Execute(null);

        Assert.NotNull(item.LastPlayTask);
        await item.LastPlayTask!;

        Assert.Single(service.Invocations);
        var invocation = service.Invocations[0];
        Assert.Equal("Greeting", invocation.Name);
        Assert.Equal(MacroScope.Global, invocation.Scope);
    }

    [Fact]
    public async Task EmptyStorage_IsEmptyTrue()
    {
        var storage = new FakeStorage(repoAvailable: false);
        using var vm = new MacrosToolWindowViewModel(storage, debounceInterval: TimeSpan.Zero);
        await vm.LoadAsync();

        Assert.True(vm.IsEmpty);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task LoadAsync_StorageThrows_SurfacesAsHasError()
    {
        var storage = new ThrowingStorage();
        using var vm = new MacrosToolWindowViewModel(storage, debounceInterval: TimeSpan.Zero);

        await vm.LoadAsync();

        Assert.True(vm.HasError);
        Assert.False(string.IsNullOrEmpty(vm.StatusMessage));
        Assert.Contains("Failed to load macros", vm.StatusMessage);
    }

    [Fact]
    public async Task RefreshCommand_TriggersLoad()
    {
        var storage = new FakeStorage(repoAvailable: false);
        storage.Add(MacroScope.Global, "X");

        using var vm = new MacrosToolWindowViewModel(storage, debounceInterval: TimeSpan.Zero);
        await vm.LoadAsync();
        int before = vm.LoadCount;

        storage.Add(MacroScope.Global, "Y");
        var next = vm.NextLoadAsync();
        vm.RefreshCommand.Execute(null);
        await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.True(vm.LoadCount > before);
        Assert.Equal(2, vm.Groups.Single(g => g.Scope == MacroScope.Global).Items.Count);
    }

    // ─── Test doubles ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Hand-rolled <see cref="IMacroStorage"/> double. Has the field-like
    /// <c>LibraryChanged</c> event the dispose-test inspects via reflection.
    /// </summary>
    private sealed class FakeStorage : IMacroStorage
    {
        private readonly Dictionary<(MacroScope, string), MacroDescriptor> _files = new();
        private readonly bool _repoAvailable;

        public FakeStorage(bool repoAvailable)
        {
            _repoAvailable = repoAvailable;
        }

        public string CurrentPath => $"{FakeRoot}\\current.csx";
        internal event EventHandler<MacroLibraryChangedEventArgs>? LibraryChanged;
        event EventHandler<MacroLibraryChangedEventArgs>? IMacroStorage.LibraryChanged
        {
            add => LibraryChanged += value;
            remove => LibraryChanged -= value;
        }

        public void Add(MacroScope scope, string name, long size = 128)
        {
            _files[(scope, name)] = new MacroDescriptor(
                name,
                scope,
                $"{FakeRoot}\\{scope}\\{name}.csx",
                DateTime.UtcNow,
                size);
        }

        public void RaiseChanged(MacroLibraryChangeKind kind, MacroScope scope, string name)
            => LibraryChanged?.Invoke(this, new MacroLibraryChangedEventArgs(kind, scope, name));

        public Task SaveCurrentAsync(string source, CancellationToken cancellation = default) => Task.CompletedTask;
        public Task<string?> LoadCurrentAsync(CancellationToken cancellation = default) => Task.FromResult<string?>(null);
        public Task<bool> DeleteCurrentAsync(CancellationToken cancellation = default) => Task.FromResult(false);

        public Task<IReadOnlyList<MacroDescriptor>> ListAsync(MacroScope scope, CancellationToken cancellation = default)
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
            return Task.FromResult<IReadOnlyList<MacroDescriptor>>(list);
        }

        public Task<IReadOnlyList<MacroDescriptor>> ListAllAsync(CancellationToken cancellation = default)
        {
            var combined = _files.Values
                .Where(d => d.Scope != MacroScope.Repo || _repoAvailable)
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return Task.FromResult<IReadOnlyList<MacroDescriptor>>(combined);
        }

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
    }

    private sealed class ThrowingStorage : IMacroStorage
    {
        public string CurrentPath => $"{FakeRoot}\\current.csx";
        public event EventHandler<MacroLibraryChangedEventArgs>? LibraryChanged
        {
            add { _ = value; }
            remove { _ = value; }
        }

        public Task SaveCurrentAsync(string source, CancellationToken cancellation = default) => Task.CompletedTask;
        public Task<string?> LoadCurrentAsync(CancellationToken cancellation = default) => Task.FromResult<string?>(null);
        public Task<bool> DeleteCurrentAsync(CancellationToken cancellation = default) => Task.FromResult(false);

        public Task<IReadOnlyList<MacroDescriptor>> ListAsync(MacroScope scope, CancellationToken cancellation = default)
            => throw new InvalidOperationException("simulated I/O failure");

        public Task<IReadOnlyList<MacroDescriptor>> ListAllAsync(CancellationToken cancellation = default)
            => throw new InvalidOperationException("simulated I/O failure");

        public Task<string?> LoadByNameAsync(string name, MacroScope scope, CancellationToken cancellation = default)
            => Task.FromResult<string?>(null);

        public Task SaveAsAsync(string name, string source, MacroScope scope, bool overwrite = false, CancellationToken cancellation = default)
            => Task.CompletedTask;

        public Task<bool> DeleteAsync(string name, MacroScope scope, CancellationToken cancellation = default)
            => Task.FromResult(false);

        public Task RenameAsync(string oldName, string newName, MacroScope scope, CancellationToken cancellation = default)
            => Task.CompletedTask;

        public string GetMacroPath(string name, MacroScope scope) => $"{FakeRoot}\\{scope}\\{name}.csx";
        public bool IsValidName(string name) => true;
    }

    /// <summary>
    /// Hand-rolled <see cref="IMacroService"/> double. Records every
    /// <see cref="IMacroService.PlayByNameAsync"/> call and exposes a
    /// <see cref="RaiseStateChanged"/> hook so tests can drive
    /// <see cref="IMacroService.StateChanged"/> manually.
    /// </summary>
    private sealed class FakeMacroService : IMacroService
    {
        public List<(string Name, MacroScope Scope)> Invocations { get; } = new();

        public MacroState State { get; private set; } = MacroState.Idle;
        public IRecordingSink? CurrentSession => null;
        public string? CurrentMacroSource => null;
        public string? CurrentMacroName => null;
        public string? CurrentMacroPath => null;
        public int CurrentRecordingMaxSteps => int.MaxValue;

        public event EventHandler<MacroStateChangedEventArgs>? StateChanged;
#pragma warning disable CS0067 // unused — required to satisfy the IMacroService surface
        public event EventHandler? RecordingCapReached;
        public event EventHandler<int>? RecordingStepCountChanged;
#pragma warning restore CS0067

        public void RaiseStateChanged(MacroState oldState, MacroState newState)
        {
            State = newState;
            StateChanged?.Invoke(this, new MacroStateChangedEventArgs(oldState, newState));
        }

        public Task StartRecordingAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> StopRecordingAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Macros.Engine.Player.MacroPlayResult> PlayCurrentAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task PlayNamedAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<Macros.Engine.Player.MacroPlayResult> PlayByNameAsync(string name, MacroScope scope, CancellationToken cancellation = default)
        {
            Invocations.Add((name, scope));
            return Task.FromResult(new Macros.Engine.Player.MacroPlayResult(true, null, null, TimeSpan.Zero));
        }

        public Task CancelAsync() => Task.CompletedTask;
        public void CancelActivePlay() { }
    }
}
