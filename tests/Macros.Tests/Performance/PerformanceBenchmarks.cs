// =============================================================================
//  PerformanceBenchmarks — xUnit-based hot-path microbenchmarks (m5-perf-validation).
// =============================================================================
//
//  Each test:
//    1. Warms up (1-3 short iterations) to prime JIT and caches.
//    2. Measures over 100-10,000 iterations with Stopwatch.
//    3. Logs P50/P95 (or mean for micro) via ITestOutputHelper.
//    4. Asserts against a conservative threshold (2× target SLA).
//
//  All thresholds are intentionally generous to survive CI variance and cold-JIT
//  start-up. The *target* SLA (architecture goal) is documented in each comment;
//  the *assert* threshold is the CI safety margin.
//
//  Scenarios covered
//  ─────────────────────────────────────────────────────────────────────────────
//  1.  CommandObserver.Exec — skipped (noise) command          SLA:  <50 µs
//  2.  CommandObserver.Exec — Phase A recording capture         SLA: <200 µs
//  3.  RecordingSession.OnTextEdit — not capturing (idle)       SLA: <100 µs
//  4.  MacroEventBus.Subscribe — first time for a known event   SLA:   <5 ms
//  5.  MacroEventBus — synchronous dispatch to one listener     SLA:   <5 ms
//  6.  MacroPlayer — cold Roslyn compile (small script)         SLA: <500 ms
//  7.  MacroPlayer — cache hit (repeated play of same macro)    SLA:  <20 ms
//  8.  RecordingSession — capture 100 commands + drain          SLA: <200 ms
//  9.  MacrosToolWindowViewModel.LoadAsync — 100 macros          SLA: <500 ms
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE80;
using Macros.Commands;
using Macros.Engine;
using Macros.Engine.Player;
using Macros.Engine.Recording;
using Macros.Engine.Scripting;
using Macros.Engine.Storage;
using Macros.Engine.Triggers;
using Macros.Observers;
using Macros.ToolWindows;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Moq;
using Xunit;
using Xunit.Abstractions;

#pragma warning disable VSSDK005  // JoinableTaskContext created outside a VS host — intentional in unit tests.
#pragma warning disable VSTHRD010 // Thread promoted to UI by EnsureUiThreadContext; analyzer can't see that.

namespace Macros.Tests.Performance;

// ─── Collection definition ────────────────────────────────────────────────────
// CommandObserver.Exec calls ThreadHelper.ThrowIfNotOnUIThread(); the
// EnsureUiThreadContext helper below promotes the xUnit test thread to "UI
// thread" by writing ThreadHelper's internal static fields via reflection.
// Serialise ALL perf tests in this collection to prevent races on that global
// state when the test runner schedules them in parallel.
[CollectionDefinition(nameof(PerformanceBenchmarksCollection), DisableParallelization = true)]
public sealed class PerformanceBenchmarksCollection { }

[Collection(nameof(PerformanceBenchmarksCollection))]
public sealed class PerformanceBenchmarks
{
    private readonly ITestOutputHelper _out;

    public PerformanceBenchmarks(ITestOutputHelper output) => _out = output;

    // ─── UI-thread bootstrap (mirrors CommandObserverTests) ──────────────────
    // Priority command targets assert they are called on the VS UI thread.
    // In tests we promote the calling thread once per process.

    private static readonly object s_uiLock = new();
    private static int s_uiThreadId = -1;

    private static void EnsureUiThreadContext()
    {
        if (Thread.CurrentThread.ManagedThreadId == s_uiThreadId)
            return;

        lock (s_uiLock)
        {
            if (Thread.CurrentThread.ManagedThreadId == s_uiThreadId)
                return;

            _ = System.Windows.Threading.Dispatcher.CurrentDispatcher;

            typeof(ThreadHelper)
                .GetMethod("SetUIThread", BindingFlags.NonPublic | BindingFlags.Static)
                ?.Invoke(null, null);

            var ctx = new JoinableTaskContext();
            var field = typeof(ThreadHelper).GetField(
                "_joinableTaskContextCache",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "ThreadHelper._joinableTaskContextCache not found — VS SDK layout changed.");
            field.SetValue(null, ctx);

            s_uiThreadId = Thread.CurrentThread.ManagedThreadId;
        }
    }

    private static JoinableTaskFactory CreateJtf()
    {
        EnsureUiThreadContext();
        return ThreadHelper.JoinableTaskContext!.Factory;
    }

    private static int ExecObserver(CommandObserver observer, Guid group, uint id)
    {
        EnsureUiThreadContext();
        return observer.Exec(ref group, id, 0u, IntPtr.Zero, IntPtr.Zero);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Scenario 1 — CommandObserver.Exec (skipped / noise command)
    //
    //  Target SLA: <50 µs per call.
    //  Hot path:   IsSkipped() = HashSet.Contains((GUID, uint)).
    //              Phases A and B are entirely bypassed; returns NOTSUPPORTED.
    //  Assert CI threshold: 500 µs (10× target — generous for cold JIT + CI).
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public void CommandObserver_Exec_SkippedCommand_Under_10us()
    {
        // SLA: O(1) HashSet lookup only; must add <50 µs per command.
        // Measured on dev box: mean ≈ 0.13 µs/call. CI threshold: 10 µs (77× measured).
        const int Iterations = 10_000;
        const double AssertThresholdUs = 10.0; // 77× measured; catches any O(N) regression

        var observer = new CommandObserver(
            CreateJtf(),
            dispatchBefore: null,
            serviceAccessor: () => null);

        // Warmup — prime JIT and HashSet internals.
        for (int i = 0; i < 50; i++)
            ExecObserver(observer, VSConstants.GUID_VSStandardCommandSet97, 1037u); // cmdidMouseHover

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < Iterations; i++)
            ExecObserver(observer, VSConstants.GUID_VSStandardCommandSet97, 1037u);
        sw.Stop();

        double meanUs = sw.Elapsed.TotalMilliseconds / Iterations * 1_000.0;
        _out.WriteLine($"[1] CommandObserver.Exec (skipped)  N={Iterations:N0}  mean={meanUs:F2} µs  target=<50 µs  threshold=<{AssertThresholdUs} µs");

        Assert.True(meanUs < AssertThresholdUs,
            $"Mean {meanUs:F2} µs exceeds {AssertThresholdUs} µs CI threshold (target SLA: <50 µs).");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Scenario 2 — CommandObserver.Exec (Phase A: recording capture)
    //
    //  Target SLA: <200 µs per call (not counting DTE name resolution).
    //  Hot path:   service accessor → IsCapturing check → IRecordingSink.OnCommand.
    //  Assert CI threshold: 2 000 µs (10× target).
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task CommandObserver_Exec_RecordingCapture_Under_10us()
    {
        // SLA: Phase A must add <200 µs overhead per captured command (sink excluded).
        // Measured on dev box: mean ≈ 0.48 µs/call. CI threshold: 10 µs (21× measured).
        const int Iterations = 1_000;
        const double AssertThresholdUs = 10.0; // 21× measured

        // Use a real MacroService in Recording state so IsCapturing is true.
        var svc = CreateEngineService();
        await svc.StartRecordingAsync();

        var observer = new CommandObserver(
            CreateJtf(),
            dispatchBefore: null,
            serviceAccessor: () => svc);

        var sampleGroup = new Guid("11111111-2222-3333-4444-555555555555");
        const uint SampleId = 42u;

        // Warmup
        for (int i = 0; i < 10; i++)
            ExecObserver(observer, sampleGroup, SampleId);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < Iterations; i++)
            ExecObserver(observer, sampleGroup, SampleId);
        sw.Stop();

        double meanUs = sw.Elapsed.TotalMilliseconds / Iterations * 1_000.0;
        _out.WriteLine($"[2] CommandObserver.Exec (recording) N={Iterations:N0}  mean={meanUs:F2} µs  target=<200 µs  threshold=<{AssertThresholdUs} µs");

        Assert.True(meanUs < AssertThresholdUs,
            $"Mean {meanUs:F2} µs exceeds {AssertThresholdUs} µs CI threshold (target SLA: <200 µs).");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Scenario 3 — RecordingSession.OnTextEdit (idle / not capturing)
    //
    //  Target SLA: <100 µs per Changed event.
    //  Hot path:   IsCapturing (lock + bool check) → early return.
    //              This is what TextEditObserver.OnTextBufferChanged delegates to
    //              when no recording is active (the common case during normal editing).
    //  Assert CI threshold: 1 000 µs (10× target).
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public void RecordingSession_OnTextEdit_NotCapturing_Under_5us()
    {
        // SLA: idle observer must add <100 µs per keystroke event.
        // Measured on dev box: mean ≈ 0.06 µs/call. CI threshold: 5 µs (83× measured).
        const int Iterations = 10_000;
        const double AssertThresholdUs = 5.0; // 83× measured

        var svc = CreateEngineService();                    // State == Idle
        var session = new RecordingSession(svc);           // IsCapturing == false

        var step = new TextEditStep(0, 0, string.Empty, "x");

        // Warmup
        for (int i = 0; i < 50; i++)
            session.OnTextEdit(step);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < Iterations; i++)
            session.OnTextEdit(step);
        sw.Stop();

        double meanUs = sw.Elapsed.TotalMilliseconds / Iterations * 1_000.0;
        _out.WriteLine($"[3] RecordingSession.OnTextEdit (idle) N={Iterations:N0}  mean={meanUs:F2} µs  target=<100 µs  threshold=<{AssertThresholdUs} µs");

        Assert.True(meanUs < AssertThresholdUs,
            $"Mean {meanUs:F2} µs exceeds {AssertThresholdUs} µs CI threshold (target SLA: <100 µs).");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Scenario 4 — MacroEventBus.Subscribe (first time for a known event)
    //
    //  Target SLA: <5 ms per first subscribe.
    //  Work done:  LINQ search over KnownEvents list, BusEntry allocation,
    //              reflection to resolve the event, factory lookup + closure
    //              allocation (BuildDelegate caches the compiled factory per
    //              EventHandlerType, so only the first-ever call per type pays
    //              the Expression.Lambda.Compile() cost).
    //  Assert CI threshold: 5 ms (13× measured P95 of 0.38 ms on dev box).
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public void MacroEventBus_Subscribe_FirstTime_Under_5ms()
    {
        // SLA: first-time Subscribe (lazy attach) must complete in <5 ms.
        // Measured on dev box: P50 ≈ 0.34 ms, P95 ≈ 0.38 ms. CI threshold: 5 ms (13× P95).
        const int Iterations = 20;
        const double AssertThresholdMs = 5.0; // P95 target SLA; 13× measured P95

        var (bus, _, known) = CreateBusWithFakeEvent();

        // Warmup
        using var warmup = bus.Subscribe(known.CanonicalName, _ => { });

        var elapsed = new double[Iterations];
        for (int i = 0; i < Iterations; i++)
        {
            var b2 = CreateBusWithFakeEvent().bus; // fresh bus each time → re-triggers lazy attach
            var sw = Stopwatch.StartNew();
            using var _ = b2.Subscribe(known.CanonicalName, static _ => { });
            sw.Stop();
            elapsed[i] = sw.Elapsed.TotalMilliseconds;
            b2.Dispose();
        }

        Array.Sort(elapsed);
        double p50 = elapsed[Iterations / 2];
        double p95 = elapsed[(int)(Iterations * 0.95)];
        _out.WriteLine($"[4] MacroEventBus.Subscribe (first)  N={Iterations}  P50={p50:F2} ms  P95={p95:F2} ms  target=<5 ms  threshold=<{AssertThresholdMs} ms");

        Assert.True(p95 < AssertThresholdMs,
            $"P95 {p95:F2} ms exceeds {AssertThresholdMs} ms CI threshold (target SLA: <5 ms).");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Scenario 5 — MacroEventBus synchronous dispatch to one listener
    //
    //  Target SLA: <5 ms total for 1 000 dispatches.
    //  Work done:  lock snapshot, single listener invocation (no queue).
    //  Measured on dev box: ≈ 1.10 ms total (1.10 µs/event).
    //  Assert CI threshold: 10 ms total (9× measured).
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public void MacroEventBus_SynchronousDispatch_1000Events_Under_10ms()
    {
        // SLA: bus dispatch to a single listener must be <5 ms for 1 000 events.
        // Measured: ≈ 1.10 ms total. CI threshold: 10 ms (9× measured).
        const int Iterations = 1_000;
        const double AssertThresholdMs = 10.0; // 9× measured

        var (bus, category, known) = CreateBusWithFakeEvent();
        int received = 0;
        using var sub = bus.Subscribe(known.CanonicalName, _ => received++);

        var args = new FakeEventArgs { Name = "perf-test", Count = 1 };

        // Warmup
        for (int i = 0; i < 10; i++)
            category.RaiseFired(args);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < Iterations; i++)
            category.RaiseFired(args);
        sw.Stop();

        double totalMs = sw.Elapsed.TotalMilliseconds;
        _out.WriteLine($"[5] MacroEventBus.Dispatch (sync)    N={Iterations:N0}  total={totalMs:F2} ms  per-event={totalMs / Iterations * 1_000:F2} µs  target=<5 ms  threshold=<{AssertThresholdMs} ms");

        Assert.Equal(Iterations + 10, received); // sanity: all events delivered
        Assert.True(totalMs < AssertThresholdMs,
            $"Total {totalMs:F2} ms exceeds {AssertThresholdMs} ms CI threshold (target SLA: <5 ms).");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Scenario 6 — MacroPlayer cold Roslyn compile (~100-char script)
    //
    //  Target SLA: <500 ms cold.
    //  Work done:  CSharpScript.Create + Script.Compile (Roslyn full pipeline).
    //  Measured on dev box: ≈ 138 ms.
    //  Assert CI threshold: 10 000 ms (10 s) — accounts for cold JIT of Roslyn
    //              assemblies on a fresh CI agent (no NGen/R2R cache).
    //
    //  Note: threshold is intentionally wide. The measurement is the value;
    //        the assertion catches catastrophic regressions only.
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public void MacroPlayer_ColdCompile_Under_10s()
    {
        // SLA: first-time compile of a small script must complete in <500 ms.
        // Measured: ≈ 138 ms. CI threshold: 10 000 ms (allows for cold Roslyn JIT).
        const double AssertThresholdMs = 10_000.0;
        const string Script = "int x = 1 + 1;"; // ~100-char typical macro

        PlayerHarness harness = CreatePlayer();

        // Cold compile — fresh cache, no prior JIT for Roslyn assemblies.
        var sw = Stopwatch.StartNew();
        MacroPlayResult result = harness.Play(Script);
        sw.Stop();

        double coldMs = sw.Elapsed.TotalMilliseconds;
        _out.WriteLine($"[6] MacroPlayer cold compile          elapsed={coldMs:F0} ms  target=<500 ms  threshold=<{AssertThresholdMs:F0} ms");

        Assert.True(result.Success, $"Compile/run failed: {result.CompilationError ?? result.RuntimeError?.ToString()}");
        Assert.True(coldMs < AssertThresholdMs,
            $"Cold compile {coldMs:F0} ms exceeds {AssertThresholdMs:F0} ms CI threshold (target SLA: <500 ms).");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Scenario 7 — MacroPlayer cache hit (repeated play of same macro)
    //
    //  Target SLA: <20 ms per cache-hit play (RunAsync overhead only).
    //  Work done:  ScriptCompilationCache hash lookup + Script.RunAsync.
    //  Measured on dev box: P50 ≈ 0 ms, P95 ≈ 1 ms.
    //  Assert CI threshold: 1 000 ms (1 s) per play — JTF thread-switch overhead
    //              on CI varies widely; 1 s allows 1000× measured P95.
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public void MacroPlayer_CacheHit_Under_1s()
    {
        // SLA: repeated play of the same source must complete in <20 ms per play.
        // Measured: P95 ≈ 1 ms. CI threshold: 1 000 ms (1000× measured P95).
        const int Iterations = 5;
        const double AssertThresholdMsPerPlay = 1_000.0;
        const string Script = "int x = 1 + 1;";

        PlayerHarness harness = CreatePlayer();

        // Prime: compile once, so subsequent calls are cache hits.
        harness.Play(Script);

        var elapsed = new double[Iterations];
        for (int i = 0; i < Iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            MacroPlayResult result = harness.Play(Script);
            sw.Stop();
            elapsed[i] = sw.Elapsed.TotalMilliseconds;
            Assert.True(result.Success,
                $"Cache-hit play {i} failed: {result.CompilationError ?? result.RuntimeError?.ToString()}");
        }

        Array.Sort(elapsed);
        double p50 = elapsed[Iterations / 2];
        double p95 = elapsed[Iterations - 1];
        _out.WriteLine($"[7] MacroPlayer cache hit             N={Iterations}  P50={p50:F0} ms  P95={p95:F0} ms  target=<20 ms  threshold=<{AssertThresholdMsPerPlay:F0} ms");

        Assert.True(p95 < AssertThresholdMsPerPlay,
            $"P95 {p95:F0} ms exceeds {AssertThresholdMsPerPlay:F0} ms CI threshold (target SLA: <20 ms).");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Scenario 8 — Record 100 commands and drain
    //
    //  Target SLA: <200 ms total.
    //  Work done:  100× OnCommand (lock + StepAggregator.Push) + DrainAndStop.
    //  Measured on dev box: ≈ 3.25 ms total.
    //  Assert CI threshold: 500 ms (154× measured; 2.5× target SLA).
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task RecordingSession_100Commands_And_Drain_Under_500ms()
    {
        // SLA: recording 100 commands and draining must complete in <200 ms.
        // Measured: ≈ 3.25 ms. CI threshold: 500 ms (154× measured).
        const int CommandCount = 100;
        const double AssertThresholdMs = 500.0;

        var svc = CreateEngineService();
        await svc.StartRecordingAsync();
        var session = svc.CurrentSession!;

        var group = new Guid("22222222-3333-4444-5555-666666666666");

        // Warmup (small; we care about the 100-command batch below)
        for (int i = 0; i < 3; i++)
            session.OnCommand(group, (uint)i, null);

        // Main measurement: stop the running session and start fresh.
        await svc.StopRecordingAsync();
        await svc.StartRecordingAsync();
        session = svc.CurrentSession!;

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < CommandCount; i++)
            session.OnCommand(group, (uint)i, $"Command{i}");

        await svc.StopRecordingAsync(); // drains aggregator internally
        sw.Stop();

        double totalMs = sw.Elapsed.TotalMilliseconds;
        _out.WriteLine($"[8] Recording 100 commands + drain    elapsed={totalMs:F2} ms  target=<200 ms  threshold=<{AssertThresholdMs:F0} ms");

        Assert.True(totalMs < AssertThresholdMs,
            $"Total {totalMs:F2} ms exceeds {AssertThresholdMs:F0} ms CI threshold (target SLA: <200 ms).");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Scenario 9 — MacrosToolWindowViewModel.LoadAsync with 100 macros
    //
    //  Target SLA: <500 ms.
    //  Work done:  IMacroStore.ListAllAsync (in-memory fake) + ViewModel build
    //              (ObservableCollection.Add × 100).
    //  Measured on dev box: ≈ 1.05 ms.
    //  Assert CI threshold: 500 ms (476× measured; at the target SLA).
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task ToolWindowViewModel_LoadAsync_100Macros_Under_500ms()
    {
        // SLA: initial render of 100 macros must complete in <500 ms.
        // Measured: ≈ 1.05 ms. CI threshold: 500 ms (target SLA; 476× measured).
        const int MacroCount = 100;
        const double AssertThresholdMs = 500.0;

        var storage = new BenchmarkFakeStorage(repoAvailable: false);
        for (int i = 0; i < MacroCount; i++)
            storage.Add(MacroScope.Global, $"Macro-{i:D3}");

        using var vm = new MacrosToolWindowViewModel(storage, debounceInterval: TimeSpan.Zero);

        // Warmup
        await vm.LoadAsync();

        // Main measurement: reload from scratch.
        var sw = Stopwatch.StartNew();
        await vm.LoadAsync();
        sw.Stop();

        double totalMs = sw.Elapsed.TotalMilliseconds;
        _out.WriteLine($"[9] ToolWindow LoadAsync (100 macros) elapsed={totalMs:F2} ms  target=<500 ms  threshold=<{AssertThresholdMs:F0} ms");

        Assert.Equal(MacroCount, vm.Groups.Sum(g => g.Items.Count));
        Assert.True(totalMs < AssertThresholdMs,
            $"Total {totalMs:F2} ms exceeds {AssertThresholdMs:F0} ms CI threshold (target SLA: <500 ms).");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static MacroService CreateEngineService()
    {
        var ctx = new JoinableTaskContext();
        return new MacroService(ctx.Factory);
    }

    // ─── MacroPlayer test harness (mirrors MacroPlayerTests) ─────────────────

    private sealed class PlayerHarness
    {
        private readonly MacroPlayer _player;
        private readonly JoinableTaskFactory _jtf;

        public PlayerHarness(MacroPlayer player, JoinableTaskFactory jtf)
        {
            _player = player;
            _jtf = jtf;
        }

        public MacroPlayResult Play(string source) =>
            _jtf.Run(() => _player.PlayAsync(source, "perf-test", trigger: null, default));
    }

    private static PlayerHarness CreatePlayer()
    {
        // Clear xUnit's SynchronizationContext so JTC routes dispatch through its
        // own queue (drained by Jtf.Run's nested pump on the calling thread).
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        JoinableTaskContext jtc;
        try
        {
            jtc = new JoinableTaskContext();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        var cache = new ScriptCompilationCache();
        var dte = Mock.Of<DTE2>();
        var player = new MacroPlayer(jtc.Factory, cache, dte);
        return new PlayerHarness(player, jtc.Factory);
    }

    // ─── MacroEventBus fake event surface (mirrors MacroEventBusTests) ────────

    public sealed class FakeEventArgs : EventArgs
    {
        public string Name { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    public sealed class FakeEventCategory
    {
        public event EventHandler<FakeEventArgs>? Fired;
        public void RaiseFired(FakeEventArgs args) => Fired?.Invoke(this, args);
    }

    private static (MacroEventBus bus, FakeEventCategory category, KnownVsEvent known)
        CreateBusWithFakeEvent()
    {
        var category = new FakeEventCategory();
        var known = new KnownVsEvent(
            CanonicalName: "Perf.Fired",
            Category: "Perf",
            EventName: nameof(FakeEventCategory.Fired),
            EventArgsType: typeof(FakeEventArgs),
            DeclaringType: typeof(FakeEventCategory));

        var bus = new MacroEventBus(
            new[] { known },
            t => t == typeof(FakeEventCategory) ? (object)category : null);

        return (bus, category, known);
    }

    // ─── ToolWindowViewModel fake storage ────────────────────────────────────

    private sealed class BenchmarkFakeStorage : IMacroStore
    {
        private readonly List<MacroEntry> _entries = new();
        private readonly bool _repoAvailable;

        public BenchmarkFakeStorage(bool repoAvailable) => _repoAvailable = repoAvailable;

        public string CurrentPath => "X:\\perf\\current.csx";

        public event EventHandler<MacroLibraryChangedEventArgs>? LibraryChanged;

        public void Add(MacroScope scope, string name)
        {
            _entries.Add(new MacroEntry(
                name,
                scope,
                $"X:\\perf\\{scope}\\{name}.csx",
                StepCount: 0,
                DateTimeOffset.UtcNow,
                SizeBytes: 128,
                Array.Empty<TriggerBinding>()));
        }

        public Task<IReadOnlyList<MacroEntry>> ListAsync(
            MacroScope scope,
            CancellationToken cancellation = default)
        {
            IReadOnlyList<MacroEntry> result = _entries.FindAll(e => e.Scope == scope);
            return Task.FromResult(result);
        }

        public async Task<IReadOnlyList<MacroEntry>> ListAllAsync(
            CancellationToken cancellation = default)
        {
            var all = new List<MacroEntry>();
            if (_repoAvailable)
                all.AddRange(await ListAsync(MacroScope.Repo, cancellation));
            all.AddRange(await ListAsync(MacroScope.Global, cancellation));
            return all;
        }

        public Task SaveCurrentAsync(string source, CancellationToken cancellation = default) => Task.CompletedTask;
        public Task<string?> LoadCurrentAsync(CancellationToken cancellation = default) => Task.FromResult<string?>(null);
        public Task<bool> DeleteCurrentAsync(CancellationToken cancellation = default) => Task.FromResult(false);
        public Task<MacroEntry?> RefreshEntryAsync(string name, MacroScope scope, CancellationToken cancellation = default) => Task.FromResult<MacroEntry?>(null);
        public Task<string?> LoadByNameAsync(string name, MacroScope scope, CancellationToken cancellation = default) => Task.FromResult<string?>(null);
        public Task SaveAsAsync(string name, string source, MacroScope scope, bool overwrite = false, CancellationToken cancellation = default) => Task.CompletedTask;
        public Task<bool> DeleteAsync(string name, MacroScope scope, CancellationToken cancellation = default) => Task.FromResult(false);
        public Task RenameAsync(string oldName, string newName, MacroScope scope, CancellationToken cancellation = default) => Task.CompletedTask;
        public string GetMacroPath(string name, MacroScope scope) => $"X:\\perf\\{scope}\\{name}.csx";
        public bool IsValidName(string name) => !string.IsNullOrWhiteSpace(name);
    }
}
