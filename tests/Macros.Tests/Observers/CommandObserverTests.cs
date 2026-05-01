using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Macros.Commands;
using Macros.Engine;
using Macros.Engine.Player;
using Macros.Engine.Recording;
using Macros.Engine.Storage;
using Macros.Observers;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Xunit;

#pragma warning disable VSSDK005 // ThreadHelper.JoinableTaskContext is not available in unit tests.

namespace Macros.Tests.Observers;

/// <summary>
/// Marker collection that disables parallel execution for tests that mutate the
/// process-global <see cref="ThreadHelper"/> static (uiThreadDispatcher and the
/// JoinableTaskContext cache) via reflection. Other test classes in this assembly
/// don't touch ThreadHelper, but if xUnit dispatches CommandObserverTests methods
/// onto thread-pool threads in parallel with each other (or with classes that
/// instantiate Dispatcher.CurrentDispatcher on a different thread), the captured
/// uiThreadDispatcher can drift and cause spurious "must be called on UI thread"
/// failures. Forcing serialisation guarantees a single deterministic UI thread for
/// the duration of these tests.
/// </summary>
[CollectionDefinition(nameof(CommandObserverTestsCollection), DisableParallelization = true)]
public sealed class CommandObserverTestsCollection
{
}

/// <summary>
/// Pins the contract of <see cref="CommandObserver"/>'s three-phase Exec walk.
/// </summary>
/// <remarks>
/// The observer is exercised through the test-only internal constructor that injects
/// both phase hooks (the recording-sink accessor and the BeforeCommand dispatcher) as
/// delegates — so these tests never touch the VS service container, never depend on a
/// real <see cref="Macros.Triggers.CommandTriggerDispatcher"/>, and never spin up a VS
/// host. Each test asserts both the integer return value of <c>Exec</c> AND the side
/// effects on the fakes, so a regression that flips the order of phases (or accidentally
/// swallows a cancel signal) fails loudly.
/// </remarks>
[Collection(nameof(CommandObserverTestsCollection))]
public sealed class CommandObserverTests
{
    private static readonly Guid SampleGroup = new("11111111-2222-3333-4444-555555555555");
    private const uint SampleId = 42;

    private const int OLECMDERR_E_NOTSUPPORTED =
        unchecked((int)0x80040100);

    /// <summary>
    /// Initializes <see cref="ThreadHelper.JoinableTaskContext"/> on first use so the
    /// production-side <c>ThreadHelper.ThrowIfNotOnUIThread()</c> assertions inside
    /// <see cref="CommandObserver.Exec"/> succeed when called from the test thread.
    /// The setter is internal in the VS SDK; reflection writes the backing field
    /// directly. The JTC must be constructed on the SAME thread that will later run
    /// the tests, because <c>JoinableTaskContext.IsOnMainThread</c> compares against
    /// the constructor thread — so we initialize lazily from inside <see cref="Exec"/>
    /// (which is itself invoked on the test thread). Idempotent across the whole test
    /// process; later threads simply observe the already-set context.
    /// </summary>
    private static readonly object s_uiThreadLock = new();
    private static int s_uiThreadId = -1;

    private static void EnsureUiThreadContext()
    {
        // Fast path: we're already on the thread that owns the JTC. Avoid touching
        // reflection on every Exec call.
        if (Thread.CurrentThread.ManagedThreadId == s_uiThreadId)
        {
            return;
        }

        lock (s_uiThreadLock)
        {
            if (Thread.CurrentThread.ManagedThreadId == s_uiThreadId)
            {
                return;
            }

            // Touch the WPF Dispatcher first — accessing CurrentDispatcher creates one
            // for the calling thread on demand. ThreadHelper.SetUIThread captures the
            // current Dispatcher; without one ThrowIfNotOnUIThread can't be satisfied.
            _ = System.Windows.Threading.Dispatcher.CurrentDispatcher;

            // Promote this thread to the UI thread (sets the static
            // ThreadHelper.uiThreadDispatcher to the current Dispatcher).
            var setUI = typeof(ThreadHelper).GetMethod(
                "SetUIThread",
                BindingFlags.NonPublic | BindingFlags.Static);
            setUI?.Invoke(null, parameters: null);

            // Initialize the JoinableTaskContext cache so the public
            // ThreadHelper.JoinableTaskContext property doesn't NullReferenceException
            // when production code reads it from inside Exec. The setter is internal
            // in the VS SDK; reflection writes the backing field directly.
            var ctx = new JoinableTaskContext();
            var field = typeof(ThreadHelper).GetField(
                "_joinableTaskContextCache",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (field is null)
            {
                throw new InvalidOperationException(
                    "ThreadHelper._joinableTaskContextCache field not found — VS SDK layout changed.");
            }
            field.SetValue(null, ctx);

            s_uiThreadId = Thread.CurrentThread.ManagedThreadId;
        }
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static JoinableTaskFactory CreateJtf()
    {
        EnsureUiThreadContext();
        return ThreadHelper.JoinableTaskContext!.Factory;
    }

    private static int Exec(CommandObserver observer, Guid group, uint id)
    {
        // Lazily promote the calling thread to "UI" thread so the production-side
        // ThreadHelper.ThrowIfNotOnUIThread() inside Exec succeeds. Test threads can
        // legitimately differ from the static-ctor thread under xUnit's parallel
        // collection scheduler — initialize from inside Exec to be safe.
        EnsureUiThreadContext();

#pragma warning disable VSTHRD010 // EnsureUiThreadContext promoted this thread; the analyzer can't see that.
        return observer.Exec(ref group, id, 0u, IntPtr.Zero, IntPtr.Zero);
#pragma warning restore VSTHRD010
    }

    // ---------------------------------------------------------------------------
    // Phase A — Recording
    // ---------------------------------------------------------------------------

    [Fact]
    public void PhaseA_NoService_NoRecord_PassThrough()
    {
        // Service hasn't been resolved yet (or failed to resolve). The observer must
        // simply skip Phase A — no exception, no allocation — and fall through to Phase C.
        var observer = new CommandObserver(
            CreateJtf(),
            dispatchBefore: null,
            serviceAccessor: () => null);

        var result = Exec(observer, SampleGroup, SampleId);

        Assert.Equal(OLECMDERR_E_NOTSUPPORTED, result);
    }

    [Fact]
    public void PhaseA_NullCurrentSession_NoRecord_PassThrough()
    {
        // Service is up, but the engine is idle (no recording session). Same outcome
        // as the no-service case: no record, return NOTSUPPORTED.
        var service = new FakeMacroService(currentSession: null);
        var observer = new CommandObserver(
            CreateJtf(),
            dispatchBefore: null,
            serviceAccessor: () => service);

        var result = Exec(observer, SampleGroup, SampleId);

        Assert.Equal(OLECMDERR_E_NOTSUPPORTED, result);
    }

    [Fact]
    public void PhaseA_NotCapturing_NoRecord_PassThrough()
    {
        // A session exists but IsCapturing is false (e.g. ReplayGuard.IsReplaying or a
        // mid-stop transition). Phase A must respect the flag and skip.
        var sink = new FakeRecordingSink { IsCapturing = false };
        var service = new FakeMacroService(currentSession: sink);
        var observer = new CommandObserver(
            CreateJtf(),
            dispatchBefore: null,
            serviceAccessor: () => service);

        var result = Exec(observer, SampleGroup, SampleId);

        Assert.Equal(OLECMDERR_E_NOTSUPPORTED, result);
        Assert.Empty(sink.Recorded);
    }

    [Fact]
    public void PhaseA_Capturing_PushesCommandIntoSink()
    {
        var sink = new FakeRecordingSink { IsCapturing = true };
        var service = new FakeMacroService(currentSession: sink);
        var observer = new CommandObserver(
            CreateJtf(),
            dispatchBefore: null,
            serviceAccessor: () => service);

        var result = Exec(observer, SampleGroup, SampleId);

        Assert.Equal(OLECMDERR_E_NOTSUPPORTED, result);
        var only = Assert.Single(sink.Recorded);
        Assert.Equal(SampleGroup, only.Group);
        Assert.Equal(SampleId, only.Id);
    }

    [Fact]
    public void PhaseA_SinkThrows_StillReturnsPassThrough()
    {
        // Phase A is the hot path; an exception thrown by the sink (or the service
        // accessor) must NEVER break the command chain — Exec must still return
        // OLECMDERR_E_NOTSUPPORTED so the user's command runs normally.
        var sink = new FakeRecordingSink
        {
            IsCapturing = true,
            OnCommandHook = (_, _, _) => throw new InvalidOperationException("sink boom"),
        };
        var service = new FakeMacroService(currentSession: sink);
        var observer = new CommandObserver(
            CreateJtf(),
            dispatchBefore: null,
            serviceAccessor: () => service);

        var result = Exec(observer, SampleGroup, SampleId);

        Assert.Equal(OLECMDERR_E_NOTSUPPORTED, result);
    }

    [Fact]
    public void PhaseA_ServiceAccessorThrows_StillReturnsPassThrough()
    {
        var observer = new CommandObserver(
            CreateJtf(),
            dispatchBefore: null,
            serviceAccessor: () => throw new InvalidOperationException("accessor boom"));

        var result = Exec(observer, SampleGroup, SampleId);

        Assert.Equal(OLECMDERR_E_NOTSUPPORTED, result);
    }

    // ---------------------------------------------------------------------------
    // Phase B — BeforeCommand dispatch
    // ---------------------------------------------------------------------------

    [Fact]
    public void PhaseB_NullDispatcher_NoCancel_PassThrough()
    {
        // No dispatcher means Phase B is a no-op; Exec must always pass through.
        var observer = new CommandObserver(
            CreateJtf(),
            dispatchBefore: null,
            serviceAccessor: () => null);

        var result = Exec(observer, SampleGroup, SampleId);

        Assert.Equal(OLECMDERR_E_NOTSUPPORTED, result);
    }

    [Fact]
    public void PhaseB_DispatcherReturnsTrue_ReturnsOK_CancelsCommand()
    {
        // A BeforeCommand macro called Trigger.CancelCommand(). Exec MUST return S_OK to
        // claim the command and pre-empt the rest of the priority-target chain.
        var observer = new CommandObserver(
            CreateJtf(),
            dispatchBefore: (_, _) => true,
            serviceAccessor: () => null);

        var result = Exec(observer, SampleGroup, SampleId);

        Assert.Equal(VSConstants.S_OK, result);
    }

    [Fact]
    public void PhaseB_DispatcherReturnsFalse_NoCancel_PassThrough()
    {
        var observer = new CommandObserver(
            CreateJtf(),
            dispatchBefore: (_, _) => false,
            serviceAccessor: () => null);

        var result = Exec(observer, SampleGroup, SampleId);

        Assert.Equal(OLECMDERR_E_NOTSUPPORTED, result);
    }

    [Fact]
    public void PhaseB_DispatcherThrows_FailSafe_PassThrough()
    {
        // A buggy dispatcher must NEVER cancel the user's command. Any thrown exception
        // is caught and treated as "do not cancel".
        var observer = new CommandObserver(
            CreateJtf(),
            dispatchBefore: (_, _) => throw new InvalidOperationException("dispatcher boom"),
            serviceAccessor: () => null);

        var result = Exec(observer, SampleGroup, SampleId);

        Assert.Equal(OLECMDERR_E_NOTSUPPORTED, result);
    }

    [Fact]
    public void PhaseB_RunsAfterPhaseA_RecordingHappensEvenWhenCancelled()
    {
        // Spec: Phase A runs FIRST and unconditionally — a recorded macro is the log of
        // what the user attempted, even if a BeforeCommand validator later cancelled it.
        // This pins the phase order so a future refactor can't silently invert them.
        var sink = new FakeRecordingSink { IsCapturing = true };
        var service = new FakeMacroService(currentSession: sink);
        var observer = new CommandObserver(
            CreateJtf(),
            dispatchBefore: (_, _) => true,
            serviceAccessor: () => service);

        var result = Exec(observer, SampleGroup, SampleId);

        Assert.Equal(VSConstants.S_OK, result);
        Assert.Single(sink.Recorded); // Phase A ran before Phase B's cancel claim.
    }

    // ---------------------------------------------------------------------------
    // Skip list
    // ---------------------------------------------------------------------------

    [Fact]
    public void SkipList_MacrosCommandSet_BypassesBothPhases()
    {
        // A command in the Macros.* command set must NOT be recorded (record-the-recorder
        // loop) AND must NOT trigger BeforeCommand dispatch (a macro can't usefully
        // cancel its own invocation). The dispatcher hook is wired to throw so the test
        // would fail loudly if Phase B were reached.
        var sink = new FakeRecordingSink { IsCapturing = true };
        var service = new FakeMacroService(currentSession: sink);
        var dispatcherCalled = false;
        var observer = new CommandObserver(
            CreateJtf(),
            dispatchBefore: (_, _) => { dispatcherCalled = true; return true; },
            serviceAccessor: () => service);

        var macrosCommandSet = new Guid(PackageGuids.CommandSetGuidString);
        var result = Exec(observer, macrosCommandSet, 0x0100u);

        Assert.Equal(OLECMDERR_E_NOTSUPPORTED, result);
        Assert.Empty(sink.Recorded);
        Assert.False(dispatcherCalled, "Phase B must not run for Macros.* commands");
    }

    [Fact]
    public void SkipList_NoiseCommand_BypassesBothPhases()
    {
        // cmdidMouseHover is in the noise skip list. Same expectations as the
        // Macros.* skip case.
        var sink = new FakeRecordingSink { IsCapturing = true };
        var service = new FakeMacroService(currentSession: sink);
        var dispatcherCalled = false;
        var observer = new CommandObserver(
            CreateJtf(),
            dispatchBefore: (_, _) => { dispatcherCalled = true; return true; },
            serviceAccessor: () => service);

        var result = Exec(observer, VSConstants.GUID_VSStandardCommandSet97, 1037u /* cmdidMouseHover */);

        Assert.Equal(OLECMDERR_E_NOTSUPPORTED, result);
        Assert.Empty(sink.Recorded);
        Assert.False(dispatcherCalled, "Phase B must not run for noise commands");
    }

    // ---------------------------------------------------------------------------
    // ResolveCommandName fallbacks (exercised indirectly through Phase A)
    // ---------------------------------------------------------------------------

    [Fact]
    public void ResolveCommandName_CacheHit_PassesNameToSink()
    {
        // Prime the singleton cache so Phase A's name lookup hits without falling
        // through to DTE (which is unavailable in tests anyway).
        const string commandName = "Test.SampleCommand";
        CommandNameCache.Instance.TryAddName(SampleGroup, SampleId, commandName);

        var sink = new FakeRecordingSink { IsCapturing = true };
        var service = new FakeMacroService(currentSession: sink);
        var observer = new CommandObserver(
            CreateJtf(),
            dispatchBefore: null,
            serviceAccessor: () => service);

        var result = Exec(observer, SampleGroup, SampleId);

        Assert.Equal(OLECMDERR_E_NOTSUPPORTED, result);
        var only = Assert.Single(sink.Recorded);
        Assert.Equal(commandName, only.Name);
    }

    [Fact]
    public void ResolveCommandName_CacheMissAndNoDte_RecordsWithNullName()
    {
        // Choose a (group, id) we are confident isn't already cached by another test
        // (cache singleton persists across tests in the assembly).
        var unknownGroup = new Guid("99999999-8888-7777-6666-555555555555");
        const uint unknownId = 0xDEADBEEFu;

        var sink = new FakeRecordingSink { IsCapturing = true };
        var service = new FakeMacroService(currentSession: sink);
        var observer = new CommandObserver(
            CreateJtf(),
            dispatchBefore: null,
            serviceAccessor: () => service);

        var result = Exec(observer, unknownGroup, unknownId);

        Assert.Equal(OLECMDERR_E_NOTSUPPORTED, result);
        var only = Assert.Single(sink.Recorded);
        // No cache entry; the DTE fallback returns null in tests (no global service
        // provider). The contract is: pass null through to the sink rather than throw.
        Assert.Null(only.Name);
    }

    // ---------------------------------------------------------------------------
    // Fakes
    // ---------------------------------------------------------------------------

    private sealed class FakeRecordingSink : IRecordingSink
    {
        public bool IsCapturing { get; set; }

        public List<(Guid Group, uint Id, string? Name)> Recorded { get; } = new();

        public Action<Guid, uint, string?>? OnCommandHook { get; set; }

        public void OnCommand(Guid group, uint id, string? canonicalName)
        {
            OnCommandHook?.Invoke(group, id, canonicalName);
            Recorded.Add((group, id, canonicalName));
        }
    }

    private sealed class FakeMacroService : IMacroService
    {
        public FakeMacroService(IRecordingSink? currentSession)
        {
            CurrentSession = currentSession;
        }

        public MacroState State => MacroState.Idle;
        public IRecordingSink? CurrentSession { get; }
        public string? CurrentMacroSource => null;
        public string? CurrentMacroName => null;
        public string? CurrentMacroPath => null;
        public int CurrentRecordingMaxSteps => int.MaxValue;

        public event EventHandler<MacroStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler? RecordingCapReached { add { } remove { } }
        public event EventHandler<int>? RecordingStepCountChanged { add { } remove { } }
        public event EventHandler<TriggeredExecutionEventArgs>? TriggeredExecutionStarted { add { } remove { } }
        public event EventHandler<TriggeredExecutionEventArgs>? TriggeredExecutionEnded { add { } remove { } }

        public Task StartRecordingAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> StopRecordingAsync(CancellationToken ct = default) => Task.FromResult(string.Empty);
        public Task<MacroPlayResult> PlayCurrentAsync(CancellationToken ct = default)
            => Task.FromResult(new MacroPlayResult(false, null, null, TimeSpan.Zero));
        public Task PlayNamedAsync(string name, CancellationToken ct = default) => Task.CompletedTask;
        public Task<MacroPlayResult> PlayByNameAsync(string name, MacroScope scope, CancellationToken cancellation = default)
            => Task.FromResult(new MacroPlayResult(false, null, null, TimeSpan.Zero));
        public Task CancelAsync() => Task.CompletedTask;
        public void CancelActivePlay() { }
    }
}
