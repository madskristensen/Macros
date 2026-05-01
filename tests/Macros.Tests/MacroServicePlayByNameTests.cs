using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine;
using Macros.Engine.Player;
using Macros.Engine.Storage;
using Macros.Engine.Triggers;
using Microsoft.VisualStudio.Threading;
using Xunit;

namespace Macros.Tests;

/// <summary>
/// Verifies the M3 <see cref="IMacroService.PlayByNameAsync"/> wiring: load via storage,
/// state cycle, current-source mirroring, and friendly failure modes.
/// </summary>
public sealed class MacroServicePlayByNameTests
{
    private static JoinableTaskFactory CreateJtf()
    {
#pragma warning disable VSSDK005
        return new JoinableTaskContext().Factory;
#pragma warning restore VSSDK005
    }

    [Fact]
    public async Task PlayByNameAsync_ValidName_LoadsFromStorage_AndPlays()
    {
        var fake = new FakeNamedStorage();
        await fake.SaveAsAsync("Greeting", "// hello\n", MacroScope.Global);

        string? observedSource = null;
        string? observedName = null;
        var player = new CapturingMacroPlayer((src, name) =>
        {
            observedSource = src;
            observedName = name;
        });

        var svc = new MacroService(
            CreateJtf(),
            playerFactory: _ => Task.FromResult<IMacroPlayer>(player),
            storage: fake);

        var result = await svc.PlayByNameAsync("Greeting", MacroScope.Global);

        Assert.True(result.Success);
        Assert.Equal("// hello\n", observedSource);
        Assert.Equal("Greeting", observedName);

        // Mirrors into CurrentMacroSource so a follow-up Play Last replays the same macro.
        Assert.Equal("// hello\n", svc.CurrentMacroSource);
        Assert.Equal("Greeting", svc.CurrentMacroName);
        Assert.Equal(MacroState.Idle, svc.State);
    }

    [Fact]
    public async Task PlayByNameAsync_MissingMacro_ReturnsSyntheticFailure()
    {
        var fake = new FakeNamedStorage();
        var svc = new MacroService(CreateJtf(), storage: fake);

        var result = await svc.PlayByNameAsync("Ghost", MacroScope.Global);

        Assert.False(result.Success);
        Assert.NotNull(result.CompilationError);
        Assert.Contains("Ghost", result.CompilationError);
        // No state cycle: failure short-circuits before transitioning to Playing.
        Assert.Equal(MacroState.Idle, svc.State);
    }

    [Fact]
    public async Task PlayByNameAsync_FromNonIdleState_Throws()
    {
        var fake = new FakeNamedStorage();
        await fake.SaveAsAsync("Anything", "// x", MacroScope.Global);

        var svc = new MacroService(CreateJtf(), storage: fake);
        await svc.StartRecordingAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.PlayByNameAsync("Anything", MacroScope.Global));
    }

    [Fact]
    public async Task PlayByNameAsync_EmptyName_Throws()
    {
        var fake = new FakeNamedStorage();
        var svc = new MacroService(CreateJtf(), storage: fake);

        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.PlayByNameAsync("", MacroScope.Global));
    }

    [Fact]
    public async Task PlayByNameAsync_NoStorage_ReturnsSyntheticFailure()
    {
        var svc = new MacroService(CreateJtf());

        var result = await svc.PlayByNameAsync("Anything", MacroScope.Global);

        Assert.False(result.Success);
        Assert.NotNull(result.CompilationError);
    }

    /// <summary>
    /// Minimal in-memory <see cref="IMacroStore"/> double that supports the named-macro
    /// API. The single-file API is left intentionally bare — these tests don't exercise it.
    /// </summary>
    private sealed class FakeNamedStorage : IMacroStore
    {
        private readonly Dictionary<(MacroScope, string), string> _files = new();

        public string CurrentPath => "X:\\fake\\current.csx";

        public Task SaveCurrentAsync(string source, CancellationToken cancellation = default)
            => Task.CompletedTask;
        public Task<string?> LoadCurrentAsync(CancellationToken cancellation = default)
            => Task.FromResult<string?>(null);
        public Task<bool> DeleteCurrentAsync(CancellationToken cancellation = default)
            => Task.FromResult(false);

        public event EventHandler<MacroLibraryChangedEventArgs>? LibraryChanged;

        public Task<IReadOnlyList<MacroEntry>> ListAsync(MacroScope scope, CancellationToken cancellation = default)
            => Task.FromResult<IReadOnlyList<MacroEntry>>(Array.Empty<MacroEntry>());
        public Task<IReadOnlyList<MacroEntry>> ListAllAsync(CancellationToken cancellation = default)
            => Task.FromResult<IReadOnlyList<MacroEntry>>(Array.Empty<MacroEntry>());

        public Task<string?> LoadByNameAsync(string name, MacroScope scope, CancellationToken cancellation = default)
        {
            _files.TryGetValue((scope, name), out var content);
            return Task.FromResult<string?>(content);
        }

        public Task SaveAsAsync(string name, string source, MacroScope scope, bool overwrite = false, CancellationToken cancellation = default)
        {
            _files[(scope, name)] = source;
            LibraryChanged?.Invoke(this, new MacroLibraryChangedEventArgs(MacroLibraryChangeKind.Added, scope, name));
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string name, MacroScope scope, CancellationToken cancellation = default)
            => Task.FromResult(_files.Remove((scope, name)));

        public Task RenameAsync(string oldName, string newName, MacroScope scope, CancellationToken cancellation = default)
            => Task.CompletedTask;

        public string GetMacroPath(string name, MacroScope scope) => $"X:\\fake\\{scope}\\{name}.csx";

        public bool IsValidName(string name) => !string.IsNullOrWhiteSpace(name);

        public Task<MacroEntry?> RefreshEntryAsync(string name, MacroScope scope, CancellationToken cancellation = default)
            => Task.FromResult<MacroEntry?>(null);
    }

    /// <summary>Captures both the source and macro-name handed to the player.</summary>
    private sealed class CapturingMacroPlayer : IMacroPlayer
    {
        private readonly Action<string, string> _capture;

        public CapturingMacroPlayer(Action<string, string> capture)
        {
            _capture = capture;
        }

        public Task<MacroPlayResult> PlayAsync(string source, string macroName, IMacroTrigger? trigger, CancellationToken cancellation)
        {
            _capture(source, macroName);
            return Task.FromResult(new MacroPlayResult(true, null, null, TimeSpan.Zero));
        }
    }
}
