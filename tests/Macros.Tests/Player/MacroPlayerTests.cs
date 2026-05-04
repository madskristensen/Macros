using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE80;
using Macros.Engine.Player;
using Macros.Engine.Recording;
using Macros.Engine.Scripting;
using Macros.Engine.Triggers;
using Microsoft.VisualStudio.Threading;
using Moq;
using Xunit;

namespace Macros.Tests.Player;

/// <summary>
/// Unit tests for <see cref="MacroPlayer"/>. These avoid touching DTE / IVsUIShell —
/// scripts here never call <see cref="Helpers"/>, so a Moq-generated <see cref="DTE2"/>
/// stub (which throws if any member is invoked) is safe to inject. Helper integration
/// requires a hosted VS and can only be smoke-tested manually inside an experimental hive.
/// </summary>
/// <remarks>
/// The player calls <c>JoinableTaskFactory.SwitchToMainThreadAsync</c> after a threadpool
/// compile hop. <see cref="JoinableTaskContext"/> created with the parameterless ctor
/// captures the test thread as the "main thread" but no message pump is running on it,
/// so a plain <c>await player.PlayAsync(...)</c> would dead-lock when the switch tries to
/// post back. Each test therefore calls <see cref="JoinableTaskFactory.Run{T}(Func{Task{T}})"/>,
/// which installs a nested message pump on the calling thread for the duration of the
/// asynchronous work — this is the same pattern that hosted VS uses for synchronous
/// invocations from the UI thread.
/// </remarks>
public sealed class MacroPlayerTests
{
    private sealed class PlayerHarness
    {
        public PlayerHarness(MacroPlayer player, ScriptCompilationCache cache, JoinableTaskFactory jtf)
        {
            Player = player;
            Cache = cache;
            Jtf = jtf;
        }

        public MacroPlayer Player { get; }

        public ScriptCompilationCache Cache { get; }

        public JoinableTaskFactory Jtf { get; }

        public MacroPlayResult Play(
            string source,
            string macroName = "test",
            IMacroTrigger? trigger = null,
            CancellationToken cancellation = default)
        {
            MacroPlayer player = Player;
            return Jtf.Run(() => player.PlayAsync(source, macroName, trigger, cancellation));
        }
    }

    private static PlayerHarness CreatePlayer()
    {
        // xUnit installs MaxConcurrencySyncContext on the test thread; JoinableTaskContext's
        // parameterless ctor captures whatever is current and routes SwitchToMainThreadAsync
        // posts through it — which dispatches to the xUnit threadpool, NOT our test thread.
        // Clear it before constructing the JTC so the JTC owns dispatch via its own
        // single-threaded queue, drained by Jtf.Run's nested pump on the test thread.
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        JoinableTaskContext jtc;
        try
        {
#pragma warning disable VSSDK005
            jtc = new JoinableTaskContext();
#pragma warning restore VSSDK005
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        var cache = new ScriptCompilationCache();
        // DTE2 is a COM interface; Moq generates a proxy that satisfies the non-null
        // constructor contract on both MacroPlayer and MacroGlobals. Tests that touch a
        // member of the mock would throw — by design they don't.
        var dte = Mock.Of<DTE2>();
        var player = new MacroPlayer(jtc.Factory, cache, dte);
        return new PlayerHarness(player, cache, jtc.Factory);
    }

    [Fact]
    public void PlayAsync_TrivialScript_ReturnsSuccess()
    {
        PlayerHarness harness = CreatePlayer();

        MacroPlayResult result = harness.Play("int x = 1 + 1;", macroName: "trivial");

        Assert.True(result.Success, result.CompilationError ?? result.RuntimeError?.ToString() ?? "no error");
        Assert.Null(result.CompilationError);
        Assert.Null(result.RuntimeError);
    }

    [Fact]
    public void PlayAsync_CompileError_ReturnsCompilationError()
    {
        PlayerHarness harness = CreatePlayer();

        MacroPlayResult result = harness.Play("this is not valid C#;", macroName: "broken");

        Assert.False(result.Success);
        Assert.NotNull(result.CompilationError);
        Assert.False(string.IsNullOrWhiteSpace(result.CompilationError));
        Assert.Null(result.RuntimeError);
    }

    [Fact]
    public void PlayAsync_RuntimeException_PopulatesRuntimeError()
    {
        PlayerHarness harness = CreatePlayer();

        MacroPlayResult result = harness.Play(
            "throw new System.InvalidOperationException(\"boom\");",
            macroName: "explodes");

        Assert.False(result.Success);
        Assert.Null(result.CompilationError);
        Assert.NotNull(result.RuntimeError);
        Assert.IsType<InvalidOperationException>(result.RuntimeError);
        Assert.Equal("boom", result.RuntimeError!.Message);
    }

    [Fact]
    public void PlayAsync_PreCancelledToken_ReturnsOperationCanceledException()
    {
        PlayerHarness harness = CreatePlayer();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        MacroPlayResult result = harness.Play(
            "int x = 1;",
            macroName: "cancelled",
            cancellation: cts.Token);

        Assert.False(result.Success);
        Assert.Null(result.CompilationError);
        Assert.NotNull(result.RuntimeError);
        Assert.IsAssignableFrom<OperationCanceledException>(result.RuntimeError);
    }

    [Fact]
    public void PlayAsync_SameSourceTwice_HitsCache()
    {
        PlayerHarness harness = CreatePlayer();
        const string source = "int y = 2 + 2;";

        Assert.Equal(0, harness.Cache.Count);

        MacroPlayResult first = harness.Play(source);
        Assert.True(first.Success);
        Assert.Equal(1, harness.Cache.Count);

        MacroPlayResult second = harness.Play(source);
        Assert.True(second.Success);

        // Second invocation must reuse the cached compilation; the cache must still hold
        // exactly one entry for this source.
        Assert.Equal(1, harness.Cache.Count);
    }

    [Fact]
    public void PlayAsync_GlobalsContext_IsVisibleToScript()
    {
        PlayerHarness harness = CreatePlayer();

        // Script throws if Context is unset or MacroName disagrees with what we passed in.
        // PlayAsync ignores ReturnValue, so we communicate failure by throwing.
        const string source = @"
if (Context is null) throw new System.Exception(""Context is null"");
if (Context.MacroName != ""hello"") throw new System.Exception(""MacroName mismatch: "" + Context.MacroName);
if (Context.TriggerKind != ""Manual"") throw new System.Exception(""TriggerKind mismatch"");
if (!Context.IsManual) throw new System.Exception(""IsManual should be true"");
";

        MacroPlayResult result = harness.Play(source, macroName: "hello");

        Assert.True(result.Success, result.RuntimeError?.Message ?? "no error");
        Assert.Null(result.RuntimeError);
    }

    [Fact]
    public void PlayAsync_AmbientGlobals_IsClearedAfterRun()
    {
        PlayerHarness harness = CreatePlayer();

        // Helpers.CurrentGlobals is internal — the dynamic script assembly cannot access it
        // directly, so we can't have the script self-report. Instead we rely on two facts:
        //   (1) the GlobalsContext test above proves the script saw a non-null Globals
        //       (it dereferenced Context.MacroName etc.), and
        //   (2) this test asserts the slot is null on the calling thread once PlayAsync
        //       returns, proving the player's finally-block ran.
        Helpers.CurrentGlobals.Value = null;

        MacroPlayResult result = harness.Play("int x = 1;", macroName: "ambient");

        Assert.True(result.Success, result.CompilationError ?? result.RuntimeError?.Message ?? "no error");
        Assert.Null(Helpers.CurrentGlobals.Value);
    }

    [Fact]
    public void PlayAsync_ReplayGuard_IsSetInsideScript_AndUnsetAfter()
    {
        PlayerHarness harness = CreatePlayer();

        // ReplayGuard is [ThreadStatic]; the in-script assertion only holds on the same
        // thread that entered the guard. Because Jtf.Run pumps on the test thread and the
        // SwitchToMainThreadAsync inside the player resumes back onto that thread before
        // RunAsync is invoked, the assertion holds here. The known gap that this guard
        // does NOT follow await-resume hops onto worker threads is documented inline in
        // MacroPlayer.cs and tracked by the m2-isreplaying-guard todo.
        const string source = @"
if (!Macros.Engine.Recording.ReplayGuard.IsReplaying)
    throw new System.Exception(""ReplayGuard.IsReplaying was false inside script"");
";

        MacroPlayResult result = harness.Play(source, macroName: "guard");

        Assert.True(result.Success, result.RuntimeError?.Message ?? "no error");
        Assert.False(ReplayGuard.IsReplaying);
    }

    [Fact]
    public void PlayAsync_TriggerBag_IsForwardedToScript()
    {
        PlayerHarness harness = CreatePlayer();
        var trigger = new CommandMacroTrigger(TriggerKind.BeforeCommand, "File.Open", DateTimeOffset.UtcNow);

        const string source = @"
if (Context.TriggerKind != ""BeforeCommand"") throw new System.Exception(""kind"");
if (Context.IsManual) throw new System.Exception(""IsManual should be false"");
if (Context.Trigger.CommandName != ""File.Open"") throw new System.Exception(""missing CommandName"");
if (Trigger.CommandName != ""File.Open"") throw new System.Exception(""Trigger shortcut mismatch"");
";

        MacroPlayResult result = harness.Play(source, macroName: "before", trigger: trigger);

        Assert.True(result.Success, result.RuntimeError?.Message ?? "no error");
    }

    [Fact]
    public async Task PlayAsync_NullSource_Throws()
    {
        PlayerHarness harness = CreatePlayer();
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Player.PlayAsync(null!, "x", trigger: null, CancellationToken.None));
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
#pragma warning disable VSSDK005
        var jtc = new JoinableTaskContext();
#pragma warning restore VSSDK005
        var cache = new ScriptCompilationCache();
        var dte = Mock.Of<DTE2>();

        Assert.Throws<ArgumentNullException>(() => new MacroPlayer(null!, cache, dte));
        Assert.Throws<ArgumentNullException>(() => new MacroPlayer(jtc.Factory, null!, dte));
        Assert.Throws<ArgumentNullException>(() => new MacroPlayer(jtc.Factory, cache, dte: null!));
    }
}
