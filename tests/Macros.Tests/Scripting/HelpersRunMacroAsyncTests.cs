using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE80;
using Macros.Engine.Player;
using Macros.Engine.Scripting;
using Macros.Engine.Storage;
using Macros.Engine.Triggers;
using Microsoft.VisualStudio.Threading;
using Moq;
using Xunit;

namespace Macros.Tests.Scripting;

/// <summary>
/// Behavioural tests for <see cref="Helpers.RunMacroAsync"/>. The helper bypasses
/// <see cref="MacroService"/>'s state machine and drives <see cref="IMacroPlayer"/> directly,
/// so the relevant contracts to lock down are: store lookup order (repo-wins), failure
/// translation, ambient-globals stack semantics, and the depth/cycle re-entrance guard.
/// </summary>
public sealed class HelpersRunMacroAsyncTests
{
    [Fact]
    public async Task RunMacroAsync_NullName_ThrowsArgumentException()
    {
        var harness = new Harness();
        Helpers.CurrentGlobals.Value = harness.Globals;
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => Helpers.RunMacroAsync("  "));
        }
        finally
        {
            Helpers.CurrentGlobals.Value = null;
        }
    }

    [Fact]
    public async Task RunMacroAsync_NoAmbientGlobals_Throws()
    {
        Helpers.CurrentGlobals.Value = null;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Helpers.RunMacroAsync("foo"));
        Assert.Contains("ambient", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunMacroAsync_MissingMacro_ThrowsInvalidOperation()
    {
        var harness = new Harness();

        Helpers.CurrentGlobals.Value = harness.Globals;
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => Helpers.RunMacroAsync("does-not-exist"));
            Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Helpers.CurrentGlobals.Value = null;
        }
    }

    [Fact]
    public async Task RunMacroAsync_RepoWinsOverGlobal()
    {
        var harness = new Harness();
        harness.Store.Set(MacroScope.Global, "fmt", "// global");
        harness.Store.Set(MacroScope.Repo, "fmt", "// repo");

        Helpers.CurrentGlobals.Value = harness.Globals;
        try
        {
            await Helpers.RunMacroAsync("fmt");
        }
        finally
        {
            Helpers.CurrentGlobals.Value = null;
        }

        Assert.Single(harness.Player.Calls);
        Assert.Equal("// repo", harness.Player.Calls[0].Source);
        Assert.Equal("fmt", harness.Player.Calls[0].MacroName);
    }

    [Fact]
    public async Task RunMacroAsync_FallsBackToGlobalWhenRepoMissing()
    {
        var harness = new Harness();
        harness.Store.Set(MacroScope.Global, "fmt", "// global");

        Helpers.CurrentGlobals.Value = harness.Globals;
        try
        {
            await Helpers.RunMacroAsync("fmt");
        }
        finally
        {
            Helpers.CurrentGlobals.Value = null;
        }

        Assert.Equal("// global", Assert.Single(harness.Player.Calls).Source);
    }

    [Fact]
    public async Task RunMacroAsync_PassesManualTrigger()
    {
        var harness = new Harness();
        harness.Store.Set(MacroScope.Global, "fmt", "// global");

        Helpers.CurrentGlobals.Value = harness.Globals;
        try
        {
            await Helpers.RunMacroAsync("fmt");
        }
        finally
        {
            Helpers.CurrentGlobals.Value = null;
        }

        var call = Assert.Single(harness.Player.Calls);
        Assert.NotNull(call.Trigger);
        Assert.True(call.Trigger!.IsManual, "Nested macro must run with a manual trigger.");
    }

    [Fact]
    public async Task RunMacroAsync_RuntimeFailureSurfacedAsException()
    {
        var harness = new Harness();
        harness.Store.Set(MacroScope.Global, "fmt", "// global");
        harness.Player.NextResult = new MacroPlayResult(
            Success: false,
            CompilationError: null,
            RuntimeError: new InvalidCastException("kaboom"),
            Duration: TimeSpan.Zero);

        Helpers.CurrentGlobals.Value = harness.Globals;
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => Helpers.RunMacroAsync("fmt"));
            Assert.Contains("kaboom", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.IsType<InvalidCastException>(ex.InnerException);
        }
        finally
        {
            Helpers.CurrentGlobals.Value = null;
        }
    }

    [Fact]
    public async Task RunMacroAsync_CompilationFailureSurfacedAsException()
    {
        var harness = new Harness();
        harness.Store.Set(MacroScope.Global, "fmt", "// global");
        harness.Player.NextResult = new MacroPlayResult(
            Success: false,
            CompilationError: "(1,1): error CS0103: oops",
            RuntimeError: null,
            Duration: TimeSpan.Zero);

        Helpers.CurrentGlobals.Value = harness.Globals;
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => Helpers.RunMacroAsync("fmt"));
            Assert.Contains("compile", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CS0103", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Helpers.CurrentGlobals.Value = null;
        }
    }

    [Fact]
    public async Task RunMacroAsync_CancellationSurfacesAsOperationCanceled()
    {
        var harness = new Harness();
        harness.Store.Set(MacroScope.Global, "fmt", "// global");
        var oce = new OperationCanceledException();
        harness.Player.NextResult = new MacroPlayResult(
            Success: false,
            CompilationError: null,
            RuntimeError: oce,
            Duration: TimeSpan.Zero);

        Helpers.CurrentGlobals.Value = harness.Globals;
        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => Helpers.RunMacroAsync("fmt"));
        }
        finally
        {
            Helpers.CurrentGlobals.Value = null;
        }
    }

    [Fact]
    public async Task RunMacroAsync_SelfCallDetectedAsCycle()
    {
        var harness = new Harness();
        harness.Store.Set(MacroScope.Global, "fmt", "// global");
        // The fake player invokes RunMacroAsync recursively so we can observe the cycle guard.
        harness.Player.OnCall = async () =>
        {
            await Helpers.RunMacroAsync("fmt");
        };

        Helpers.CurrentGlobals.Value = harness.Globals;
        try
        {
            // The outer call's OnCall throws after cycle detection in the inner call. The
            // outer surfaces this through the failure-translation path.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => Helpers.RunMacroAsync("fmt"));
            Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Helpers.CurrentGlobals.Value = null;
        }
    }

    [Fact]
    public async Task RunMacroAsync_DepthCappedAtThree()
    {
        var harness = new Harness();
        harness.Store.Set(MacroScope.Global, "a", "// a");
        harness.Store.Set(MacroScope.Global, "b", "// b");
        harness.Store.Set(MacroScope.Global, "c", "// c");
        harness.Store.Set(MacroScope.Global, "d", "// d");

        // a → b → c → d should be blocked at d (depth would become 4 if allowed; cap is 3).
        var nextNames = new Queue<string>(new[] { "b", "c", "d" });
        harness.Player.OnCall = async () =>
        {
            if (nextNames.Count > 0)
            {
                await Helpers.RunMacroAsync(nextNames.Dequeue());
            }
        };

        Helpers.CurrentGlobals.Value = harness.Globals;
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => Helpers.RunMacroAsync("a"));
            Assert.Contains("depth", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Helpers.CurrentGlobals.Value = null;
        }
    }

    [Fact]
    public async Task RunMacroAsync_PreservesParentAmbientGlobals()
    {
        // Validates the stack semantics of MacroPlayer.PlayAsync: a child invocation that goes
        // through the player must restore the parent's ambient globals on exit. We don't have a
        // real player here, so we simulate the contract: the helper itself MUST not clobber the
        // parent slot. The fake player below doesn't touch CurrentGlobals at all — but if the
        // helper's resolution / linked CTS / set-management leaks, the parent slot could end up
        // null on return. That is what we assert here.
        var harness = new Harness();
        harness.Store.Set(MacroScope.Global, "fmt", "// global");

        Helpers.CurrentGlobals.Value = harness.Globals;
        try
        {
            await Helpers.RunMacroAsync("fmt");
            Assert.Same(harness.Globals, Helpers.CurrentGlobals.Value);
        }
        finally
        {
            Helpers.CurrentGlobals.Value = null;
        }
    }

    [Fact]
    public async Task RunMacroAsync_ChainsCancellationFromContext()
    {
        // If the ambient context is already cancelled, the linked token should fire and the
        // fake player should observe a cancelled token. The fake player throws OCE itself via
        // the NextResult path; here we verify the linked token is cancelled at call time.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var harness = new Harness(cts.Token);
        harness.Store.Set(MacroScope.Global, "fmt", "// global");

        Helpers.CurrentGlobals.Value = harness.Globals;
        try
        {
            // The fake player records the token; we verify it's already cancelled on entry.
            harness.Player.NextResult = new MacroPlayResult(
                Success: false,
                CompilationError: null,
                RuntimeError: new OperationCanceledException(),
                Duration: TimeSpan.Zero);

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => Helpers.RunMacroAsync("fmt"));
        }
        finally
        {
            Helpers.CurrentGlobals.Value = null;
        }

        var call = Assert.Single(harness.Player.Calls);
        Assert.True(call.Cancellation.IsCancellationRequested);
    }

    // ─── Test harness ────────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public Harness(CancellationToken contextCancellation = default)
        {
            Store = new InMemoryStore();
            Player = new RecordingPlayer();
#pragma warning disable VSSDK005
            Jtc = new JoinableTaskContext();
#pragma warning restore VSSDK005

            var ctx = new TestContext("parent-macro", contextCancellation);
            Globals = new MacroGlobals(Mock.Of<DTE2>(), ctx)
            {
                UiThreadFactory = Jtc.Factory,
                Store = Store,
                Player = Player,
            };
        }

        public JoinableTaskContext Jtc { get; }

        public MacroGlobals Globals { get; }

        public InMemoryStore Store { get; }

        public RecordingPlayer Player { get; }
    }

    private sealed class TestContext : IMacroContext
    {
        public TestContext(string name, CancellationToken ct)
        {
            MacroName = name;
            Cancellation = ct;
        }

        public string MacroName { get; }
        public string TriggerKind => "Manual";
        public IMacroTrigger Trigger => ManualMacroTrigger.Instance;
        public bool IsManual => true;
        public CancellationToken Cancellation { get; }
    }

    private sealed class InMemoryStore : IMacroStore
    {
        private readonly Dictionary<(MacroScope, string), string> _files
            = new(EqualityComparer<(MacroScope, string)>.Default);

        public void Set(MacroScope scope, string name, string source)
            => _files[(scope, name.ToUpperInvariant())] = source;

        public string CurrentPath => "X:\\fake\\current.csx";
        public Task SaveCurrentAsync(string source, CancellationToken cancellation = default) => Task.CompletedTask;
        public Task<string?> LoadCurrentAsync(CancellationToken cancellation = default) => Task.FromResult<string?>(null);
        public Task<bool> DeleteCurrentAsync(CancellationToken cancellation = default) => Task.FromResult(false);
        public event EventHandler<MacroLibraryChangedEventArgs>? LibraryChanged { add { } remove { } }
        public Task<IReadOnlyList<MacroEntry>> ListAsync(MacroScope scope, CancellationToken cancellation = default)
            => Task.FromResult<IReadOnlyList<MacroEntry>>(Array.Empty<MacroEntry>());
        public Task<IReadOnlyList<MacroEntry>> ListAllAsync(CancellationToken cancellation = default)
            => Task.FromResult<IReadOnlyList<MacroEntry>>(Array.Empty<MacroEntry>());
        public Task<string?> LoadByNameAsync(string name, MacroScope scope, CancellationToken cancellation = default)
        {
            _files.TryGetValue((scope, name.ToUpperInvariant()), out string? content);
            return Task.FromResult<string?>(content);
        }

        public Task SaveAsAsync(string name, string source, MacroScope scope, bool overwrite = false, CancellationToken cancellation = default)
            => Task.CompletedTask;
        public Task<bool> DeleteAsync(string name, MacroScope scope, CancellationToken cancellation = default) => Task.FromResult(false);
        public Task RenameAsync(string oldName, string newName, MacroScope scope, CancellationToken cancellation = default) => Task.CompletedTask;
        public string GetMacroPath(string name, MacroScope scope) => $"X:\\fake\\{scope}\\{name}.csx";
        public bool IsValidName(string name) => !string.IsNullOrWhiteSpace(name);
        public Task<MacroEntry?> RefreshEntryAsync(string name, MacroScope scope, CancellationToken cancellation = default)
            => Task.FromResult<MacroEntry?>(null);
    }

    /// <summary>
    /// Player that captures every PlayAsync invocation. Optionally invokes <see cref="OnCall"/>
    /// inside the call so a test can simulate nested <see cref="Helpers.RunMacroAsync"/> calls.
    /// </summary>
    private sealed class RecordingPlayer : IMacroPlayer
    {
        public List<RecordedCall> Calls { get; } = new();

        public MacroPlayResult NextResult { get; set; } = new(true, null, null, TimeSpan.Zero);

        public Func<Task>? OnCall { get; set; }

        public async Task<MacroPlayResult> PlayAsync(string source, string macroName, IMacroTrigger? trigger, CancellationToken cancellation, string? csxFilePath = null)
        {
            Calls.Add(new RecordedCall(source, macroName, trigger, cancellation));
            if (OnCall is { } cb)
            {
                try
                {
                    await cb().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return new MacroPlayResult(false, null, ex, TimeSpan.Zero);
                }
            }

            return NextResult;
        }
    }

    private sealed record RecordedCall(string Source, string MacroName, IMacroTrigger? Trigger, CancellationToken Cancellation);
}
