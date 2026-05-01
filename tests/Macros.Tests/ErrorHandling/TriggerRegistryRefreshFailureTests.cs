using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine.Storage;
using Macros.Engine.Triggers;
using Microsoft.VisualStudio.Threading;
using Xunit;

namespace Macros.Tests.ErrorHandling;

/// <summary>
/// Pins the v1.0 contract that <see cref="MacroTriggerRegistry"/> NEVER propagates a
/// store enumeration failure back through the <see cref="IMacroStore.LibraryChanged"/>
/// event source. The motivating scenario: a transient I/O glitch (network share
/// flicker, antivirus scan) makes <see cref="IMacroStore.ListAllAsync"/> throw. The
/// registry must keep its previous indexes intact and survive — a thrown exception on
/// the LibraryChanged callback path would crash the watcher thread inside
/// FileSystemMacroStore.
/// </summary>
public sealed class TriggerRegistryRefreshFailureTests
{
    private static JoinableTaskFactory CreateJtf()
    {
#pragma warning disable VSSDK005 // ThreadHelper.JoinableTaskContext is not available outside a hosted VS process.
        return new JoinableTaskContext().Factory;
#pragma warning restore VSSDK005
    }

    [Fact]
    public async Task LibraryChanged_StoreThrows_DoesNotPropagate_KeepsRegistryUsable()
    {
        var store = new ThrowingStore();
        using var registry = new MacroTriggerRegistry(store, CreateJtf());

        // Wait for the initial fire-and-forget load to complete (or fail). We don't
        // care about the outcome — we care that the next LibraryChanged-driven refresh
        // (which we will arrange to throw) doesn't escape.
        await Task.Delay(50);

        // Arm the store to throw on the next ListAllAsync. RaiseLibraryChanged kicks
        // the debounce timer; after the 100ms window the registry calls ListAllAsync,
        // catches the throw, and stays alive. If the catch is missing, the throw
        // surfaces inside the JoinableTask and tears down the test.
        store.NextThrows = true;
        store.RaiseLibraryChanged(new MacroLibraryChangedEventArgs(
            MacroLibraryChangeKind.Added, MacroScope.Global, "newmacro"));

        // Wait long enough for the 100ms debounce + the refresh attempt.
        await Task.Delay(400);

        // Registry stays alive; queries still answer (returning whatever it had loaded
        // before the throw, which is empty in this test).
        Assert.Empty(registry.FindByEvent("X"));
        Assert.False(registry.HasBeforeCommand("Y"));
    }

    [Fact]
    public async Task PublicRefreshAsync_StoreThrows_PropagatesException()
    {
        // Pin the contract: public RefreshAsync (used by tests / future direct callers)
        // DOES propagate so callers can react. Only the LibraryChanged-driven path swallows.
        var store = new ThrowingStore();
        using var registry = new MacroTriggerRegistry(store, CreateJtf());

        // The constructor kicks off a fire-and-forget initial RefreshAsync; let it run
        // to completion (it sees NextThrows=false → succeeds with empty entries) before
        // we arm the throw and invoke RefreshAsync ourselves.
        await Task.Delay(50);

        store.NextThrows = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.RefreshAsync());
    }

    /// <summary>
    /// Minimal <see cref="IMacroStore"/> that can be armed to throw on
    /// <see cref="ListAllAsync"/>. Mirrors the FakeStore pattern from
    /// <c>MacroTriggerRegistryTests</c> but lives next to the regression test that
    /// owns it.
    /// </summary>
    private sealed class ThrowingStore : IMacroStore
    {
        public bool NextThrows;
        private EventHandler<MacroLibraryChangedEventArgs>? _changed;

        public event EventHandler<MacroLibraryChangedEventArgs>? LibraryChanged
        {
            add { _changed += value; }
            remove { _changed -= value; }
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

        public Task<IReadOnlyList<MacroEntry>> ListAsync(MacroScope scope, CancellationToken cancellation = default)
            => Task.FromResult<IReadOnlyList<MacroEntry>>(Array.Empty<MacroEntry>());

        public Task<IReadOnlyList<MacroEntry>> ListAllAsync(CancellationToken cancellation = default)
        {
            if (NextThrows)
            {
                NextThrows = false;
                throw new InvalidOperationException("Simulated transient I/O failure during ListAllAsync.");
            }
            return Task.FromResult<IReadOnlyList<MacroEntry>>(Array.Empty<MacroEntry>());
        }

        public Task<MacroEntry?> RefreshEntryAsync(string name, MacroScope scope, CancellationToken cancellation = default)
            => Task.FromResult<MacroEntry?>(null);
        public Task<string?> LoadByNameAsync(string name, MacroScope scope, CancellationToken cancellation = default)
            => Task.FromResult<string?>(null);
        public Task SaveAsAsync(string name, string source, MacroScope scope, bool overwrite = false, CancellationToken cancellation = default)
            => Task.CompletedTask;
        public Task<bool> DeleteAsync(string name, MacroScope scope, CancellationToken cancellation = default)
            => Task.FromResult(false);
        public Task RenameAsync(string oldName, string newName, MacroScope scope, CancellationToken cancellation = default)
            => Task.CompletedTask;
        public string GetMacroPath(string name, MacroScope scope) => $"X:\\fake\\{scope}\\{name}.csx";
        public bool IsValidName(string name) => !string.IsNullOrWhiteSpace(name);
    }
}
