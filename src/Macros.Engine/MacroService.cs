// Wired in MacrosPackage.InitializeAsync.

using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Macros.Engine.Codegen;
using Macros.Engine.Player;
using Macros.Engine.Recording;
using Macros.Engine.Storage;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace Macros.Engine;

/// <summary>
/// Default <see cref="IMacroService"/> implementation. Owns the Idle / Recording / Playing
/// state machine for the macro engine.
/// </summary>
/// <remarks>
/// <para>
/// State transitions are guarded by a single monitor (<c>_stateLock</c>) so concurrent
/// callers see consistent transitions; the held-lock window is intentionally tiny — only
/// the validate-and-flip is inside the lock. The <see cref="StateChanged"/> event is
/// raised <em>after</em> the lock is released and after the new state is committed.
/// </para>
/// <para>
/// M1 milestone scope: this class implements only the state machine plus stubs for
/// recording / playback. M2 wires the real recorder and code generator into
/// <see cref="StopRecordingAsync"/>, and M3 plugs in the Roslyn script runner inside
/// the play methods. The public surface is fixed in M1 so triggers (M4) and UI (M1)
/// can take a stable dependency.
/// </para>
/// </remarks>
public sealed class MacroService : IMacroService
{
    private readonly JoinableTaskFactory _jtf;
    private readonly Func<int>? _maxStepsProvider;
    private readonly Func<CancellationToken, Task<IMacroPlayer>> _playerFactory;
    private readonly IMacroStore? _storage;
    private readonly object _stateLock = new();
    private volatile MacroState _state = MacroState.Idle;
    private string? _currentMacroSource;
    private string? _currentMacroName;
    private volatile CancellationTokenSource? _activeCts;

    /// <summary>
    /// Initializes a new instance of the <see cref="MacroService"/> class.
    /// </summary>
    /// <param name="jtf">
    /// The package's <see cref="JoinableTaskFactory"/>. Stored for later UI-thread switches
    /// when M2+ recording observers and M3+ playback need to call into the VS shell.
    /// </param>
    /// <param name="maxStepsProvider">
    /// Optional callback invoked at the start of each recording to retrieve the current
    /// <c>MaxRecordingSteps</c> option value. Defaults to <see langword="null"/> (no cap),
    /// which is the right choice for unit tests. Production wiring lives in
    /// <c>MacrosPackage.InitializeAsync</c>: <c>() => MacrosOptions.Instance.MaxRecordingSteps</c>.
    /// </param>
    /// <param name="playerFactory">
    /// Optional async factory for resolving the <see cref="IMacroPlayer"/> at playback time.
    /// When <see langword="null"/>, the engine falls back to
    /// <c>VS.GetRequiredServiceAsync&lt;IMacroPlayer, IMacroPlayer&gt;()</c>, which requires
    /// the package to have proffered the service (see <c>MacrosPackage.InitializeAsync</c>).
    /// Tests inject a fake here so playback can be exercised without a hosted VS shell.
    /// </param>
    /// <param name="storage">
    /// Optional persistent storage. When supplied (production wiring in
    /// <c>MacrosPackage.InitializeAsync</c> hands in a <see cref="FileSystemMacroStore"/>)
    /// <see cref="StopRecordingAsync"/> writes the generated source to disk fire-and-forget
    /// so <c>Play Last</c> survives a VS restart, and <see cref="PlayCurrentAsync"/>
    /// transparently rehydrates from disk if no in-memory source is loaded yet. When
    /// <see langword="null"/> the engine stays in-memory only — the right default for unit
    /// tests that exercise the state machine without touching the filesystem.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="jtf"/> is <see langword="null"/>.</exception>
    public MacroService(
        JoinableTaskFactory jtf,
        Func<int>? maxStepsProvider = null,
        Func<CancellationToken, Task<IMacroPlayer>>? playerFactory = null,
        IMacroStore? storage = null)
    {
        _jtf = jtf ?? throw new ArgumentNullException(nameof(jtf));
        _maxStepsProvider = maxStepsProvider;
        _playerFactory = playerFactory ?? DefaultPlayerFactory;
        _storage = storage;
    }

    private static async Task<IMacroPlayer> DefaultPlayerFactory(CancellationToken ct)
    {
        // The package wires IMacroPlayer in InitializeAsync (m2-isreplaying-guard). If the
        // package hasn't proffered it yet, this will throw — the caller surfaces the failure
        // through the same MacroPlayResult / Output pane / InfoBar pipeline.
        return await VS.GetRequiredServiceAsync<IMacroPlayer, IMacroPlayer>();
    }

    /// <inheritdoc />
    public MacroState State => _state;

    /// <inheritdoc />
    public string? CurrentMacroSource => _currentMacroSource;

    /// <inheritdoc />
    public string? CurrentMacroName => _currentMacroName;

    /// <inheritdoc />
    public string? CurrentMacroPath => _storage?.CurrentPath;

    /// <summary>
    /// Gets the active <see cref="RecordingSession"/> while in <see cref="MacroState.Recording"/>,
    /// or <see langword="null"/> otherwise. Engine-internal: exposed to observers and tests
    /// inside the <c>Macros.Engine</c> assembly (and <c>Macros.Tests</c> via
    /// <c>InternalsVisibleTo</c>); external callers (the VSIX) see the same instance through
    /// <see cref="IMacroService.CurrentSession"/> as an <see cref="IRecordingSink"/>.
    /// </summary>
    internal RecordingSession? CurrentSession { get; private set; }

    /// <inheritdoc />
    IRecordingSink? IMacroService.CurrentSession => CurrentSession;

    /// <inheritdoc />
    public event EventHandler<MacroStateChangedEventArgs>? StateChanged;

    /// <inheritdoc />
    public event EventHandler? RecordingCapReached;

    /// <inheritdoc />
    public event EventHandler<int>? RecordingStepCountChanged;

    /// <inheritdoc />
    public event EventHandler<RecordingSavedEventArgs>? RecordingSaved;

    /// <inheritdoc />
    public event EventHandler<TriggeredExecutionEventArgs>? TriggeredExecutionStarted;

    /// <inheritdoc />
    public event EventHandler<TriggeredExecutionEventArgs>? TriggeredExecutionEnded;

    /// <inheritdoc />
    public int CurrentRecordingMaxSteps => CurrentSession?.MaxSteps ?? int.MaxValue;

    /// <inheritdoc />
    public Task StartRecordingAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        TransitionTo(expected: MacroState.Idle, next: MacroState.Recording);

        // The session must exist BEFORE any observer can fire. The transition above is the
        // serialization point — observers see Recording state only after this assignment, so
        // the first OnCommand call has a non-null sink to push into.
        int maxSteps = _maxStepsProvider?.Invoke() ?? int.MaxValue;
        var session = new RecordingSession(this, maxSteps);
        session.CapReached += OnSessionCapReached;
        session.StepCountChanged += OnSessionStepCountChanged;
        CurrentSession = session;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string> StopRecordingAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Capture the session before flipping state so a racing observer that sneaks past the
        // state check still finds a sink to push into; once we transition back to Idle the
        // observers' IsCapturing short-circuit makes any remaining pushes no-ops.
        var session = CurrentSession;

        TransitionTo(expected: MacroState.Recording, next: MacroState.Idle);

        // Drain after the state transition: IsCapturing is now false, so no observer thread can
        // append a step in parallel with the drain. The session reference is then dropped — the
        // GC reclaims the captured event list once the placeholder string is built.
        var steps = session?.DrainAndStop() ?? Array.Empty<RecordedStep>();
        CurrentSession = null;

        // The generator is a pure transform: steps + macro name + clock → .csx string.
        // The clock is injected so tests get deterministic headers; production calls flow
        // DateTime.UtcNow straight through. The macro name placeholder ("RecordedMacro")
        // is overwritten by the storage layer (m3-storage) once the user names the macro
        // on save; until then the header is informational only.
        var generatedName = "RecordedMacro";
        var source = CSharpCodeGenerator.Generate(steps, macroName: generatedName, utcNow: DateTime.UtcNow);
        _currentMacroSource = source;

        // Surface a friendly name so playback diagnostics (Output pane, Error List, InfoBar)
        // have something better than "Macro" to attribute failures to. The storage layer
        // (m3-storage) overrides this with the user-chosen name on save.
        _currentMacroName = generatedName;

        // Persist the just-recorded source so Play Last survives a VS restart. Fire-and-forget
        // through the JoinableTaskFactory so disk I/O doesn't block the recording-stop caller
        // (which is often on the UI thread). Failures bubble up via FileAndForget's telemetry
        // channel; the in-memory _currentMacroSource above already makes the just-recorded
        // macro playable in this session even if the write fails.
        //
        // We also save to the named library under a unique auto-incremented name so every
        // recording lands in its own slot.  SaveAsAsync raises IMacroStore.LibraryChanged,
        // which is what the Macros tool window subscribes to — without this call the tool
        // window never learns that a new macro arrived and the list stays stale after every
        // Stop Recording.
        if (_storage is not null)
        {
            var storage = _storage;
            _jtf.RunAsync(async () =>
            {
                await storage.SaveCurrentAsync(source).ConfigureAwait(false);
                var name = await GenerateUniqueRecordingNameAsync(storage).ConfigureAwait(false);
                await storage.SaveAsAsync(name, source, MacroScope.Global, overwrite: false).ConfigureAwait(false);
                var savedPath = storage.GetMacroPath(name, MacroScope.Global);
                RecordingSaved?.Invoke(this, new RecordingSavedEventArgs(savedPath));
            }).FileAndForget("Macros/Storage/SaveCurrent");
        }

        return Task.FromResult(source);
    }

    /// <inheritdoc />
    public async Task<MacroPlayResult> PlayCurrentAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Validate state BEFORE checking source so callers always see the same precedence:
        // a wrong-state call always throws regardless of whether a macro is loaded.
        // The transition itself isn't taken yet — we still need to short-circuit on no-source.
        lock (_stateLock)
        {
            if (_state != MacroState.Idle)
            {
                throw new InvalidOperationException(
                    $"Cannot play macro while in {_state} state (expected {MacroState.Idle}).");
            }
        }

        // Lazy rehydrate from disk on first play after VS restart. We only attempt this when
        // nothing has been recorded or loaded in this session yet; once an in-memory source
        // exists it always wins (it's strictly fresher than what storage holds). Failures
        // here are silent — falling through to the synthetic "no macro" failure result below
        // is the correct UX when the persisted file is missing or unreadable.
        if (_currentMacroSource is null && _storage is not null)
        {
            try
            {
                var loaded = await _storage.LoadCurrentAsync(ct).ConfigureAwait(false);
                if (loaded is not null)
                {
                    _currentMacroSource = loaded;
                    _currentMacroName ??= "RecordedMacro";
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Storage failure — leave _currentMacroSource null and fall through to the
                // standard "No macro to play" result. There's nothing actionable to surface
                // here that the failure result doesn't already convey.
            }
        }

        var source = _currentMacroSource;
        var name = _currentMacroName ?? "Macro";

        if (source is null)
        {
            // Synthetic failure result — surfaces through the same Output / Error List / InfoBar
            // pipeline as compile errors, but cheaper: no state cycle, no player resolution.
            return new MacroPlayResult(
                Success: false,
                CompilationError: "No macro to play. Record one with Ctrl+Shift+R first.",
                RuntimeError: null,
                Duration: TimeSpan.Zero);
        }

        return await PlayCoreAsync(source, name, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<MacroPlayResult> PlayByNameAsync(string name, MacroScope scope, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Macro name must be non-empty.", nameof(name));
        }

        cancellation.ThrowIfCancellationRequested();

        // Validate state up front so a wrong-state call throws cleanly without first
        // hitting the disk for a load that would only be discarded.
        lock (_stateLock)
        {
            if (_state != MacroState.Idle)
            {
                throw new InvalidOperationException(
                    $"Cannot play macro while in {_state} state (expected {MacroState.Idle}).");
            }
        }

        if (_storage is null)
        {
            // The engine was constructed without storage (in-memory-only test mode). Fail
            // through the same data-result channel the rest of the play surface uses so
            // callers don't need a separate code path.
            return new MacroPlayResult(
                Success: false,
                CompilationError: $"No storage available — cannot load macro '{name}'.",
                RuntimeError: null,
                Duration: TimeSpan.Zero);
        }

        string? source;
        try
        {
            source = await _storage.LoadByNameAsync(name, scope, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // ArgumentException = invalid name; InvalidOperationException = repo scope
            // with no solution. Surface through the result so commands / triggers handle
            // it consistently.
            return new MacroPlayResult(
                Success: false,
                CompilationError: $"Cannot load macro '{name}': {ex.Message}",
                RuntimeError: null,
                Duration: TimeSpan.Zero);
        }
        catch (IOException ex)
        {
            return new MacroPlayResult(
                Success: false,
                CompilationError: $"I/O error loading macro '{name}': {ex.Message}",
                RuntimeError: null,
                Duration: TimeSpan.Zero);
        }

        if (source is null)
        {
            return new MacroPlayResult(
                Success: false,
                CompilationError: $"Macro '{name}' not found in {scope} scope.",
                RuntimeError: null,
                Duration: TimeSpan.Zero);
        }

        // Mirror the "loaded macro becomes the current macro" semantic of PlayCurrentAsync
        // so a follow-up Ctrl+Shift+P (Play Last) replays the same macro.
        _currentMacroSource = source;
        _currentMacroName = name;

        string? csxFilePath = null;
        try { csxFilePath = _storage!.GetMacroPath(name, scope); } catch { /* best-effort */ }

        return await PlayCoreAsync(source, name, cancellation, csxFilePath).ConfigureAwait(false);
    }

    /// <summary>
    /// Shared core for the Play* methods. Caller is responsible for state validation and
    /// resolving <paramref name="source"/> / <paramref name="name"/>; this method owns the
    /// Idle→Playing→Idle transition, the linked CTS, and the player invocation.
    /// </summary>
    private async Task<MacroPlayResult> PlayCoreAsync(string source, string name, CancellationToken ct, string? csxFilePath = null)
    {
        TransitionTo(expected: MacroState.Idle, next: MacroState.Playing);
        _activeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            IMacroPlayer player = await _playerFactory(_activeCts.Token).ConfigureAwait(false);
            return await player.PlayAsync(source, name, trigger: null, _activeCts.Token, csxFilePath).ConfigureAwait(false);
        }
        finally
        {
            // Null _activeCts before disposing so a racing CancelActivePlay() sees null and
            // skips the Cancel() call rather than hitting an ObjectDisposedException.
            var cts = _activeCts;
            _activeCts = null;
            cts?.Dispose();
            TransitionTo(expected: MacroState.Playing, next: MacroState.Idle);
        }
    }

    /// <inheritdoc />
    public async Task PlayNamedAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Macro name must be non-empty.", nameof(name));
        }

        ct.ThrowIfCancellationRequested();

        TransitionTo(expected: MacroState.Idle, next: MacroState.Playing);
        try
        {
            // M3 will: load named macro from MacroStore and execute.
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
        finally
        {
            TransitionTo(expected: MacroState.Playing, next: MacroState.Idle);
        }
    }

    /// <inheritdoc />
    public Task CancelAsync()
    {
        MacroState previous;
        lock (_stateLock)
        {
            if (_state == MacroState.Idle)
            {
                return Task.CompletedTask;
            }

            previous = _state;
            _state = MacroState.Idle;
        }

        // Drop the session unconditionally — Cancel from Recording must not leak the in-flight
        // capture; Cancel from Playing leaves it null already, so this is a no-op there.
        CurrentSession = null;

        RaiseStateChanged(previous, MacroState.Idle);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void CancelActivePlay()
    {
        // _activeCts is volatile — the read is fresh. If CancelActivePlay() races with the
        // finally block that disposes the CTS, the cts?.Dispose()-then-null sequence in the
        // finally ensures _activeCts is nulled first; we can observe null here instead of a
        // disposed instance, so no ObjectDisposedException is possible.
        _activeCts?.Cancel();
    }

    /// <summary>Exposed for unit-testing only. Returns the live CTS while a play is in flight.</summary>
    internal CancellationTokenSource? ActiveCtsForTest => _activeCts;

    private void TransitionTo(MacroState expected, MacroState next)
    {
        MacroState previous;
        lock (_stateLock)
        {
            if (_state != expected)
            {
                throw new InvalidOperationException(
                    $"Cannot transition to {next} while in {_state} state (expected {expected}).");
            }

            previous = _state;
            _state = next;
        }

        RaiseStateChanged(previous, next);
    }

    private void RaiseStateChanged(MacroState oldState, MacroState newState)
    {
        StateChanged?.Invoke(this, new MacroStateChangedEventArgs(oldState, newState));
    }

    internal void RaiseTriggeredStarted(TriggeredExecutionEventArgs e)
    {
        TriggeredExecutionStarted?.Invoke(this, e);
    }

    internal void RaiseTriggeredEnded(TriggeredExecutionEventArgs e)
    {
        TriggeredExecutionEnded?.Invoke(this, e);
    }

    private void OnSessionCapReached(object? sender, EventArgs e)
    {
        // CapReached fires from the recording observer's thread, still within OnCommand/OnTextEdit.
        // StopRecordingAsync is synchronous; run it via the JoinableTaskFactory so it participates
        // in the JoinableTask graph and doesn't trigger VSTHRD110.
        _ = _jtf.RunAsync(async () =>
        {
            try
            {
                if (_state == MacroState.Recording)
                {
                    await StopRecordingAsync();
                }
            }
            catch (InvalidOperationException)
            {
                // Another caller stopped recording first — nothing to do.
            }

            RecordingCapReached?.Invoke(this, EventArgs.Empty);
        });
    }

    private void OnSessionStepCountChanged(object? sender, int count)
    {
        RecordingStepCountChanged?.Invoke(this, count);
    }

    /// <summary>
    /// Scans the global library for names matching <c>RecordedMacro</c> or
    /// <c>RecordedMacro&lt;N&gt;</c> and returns <c>RecordedMacro{max+1}</c>, ensuring
    /// each recording session lands in a unique slot (highest+1 rule — no gap-filling).
    /// </summary>
    private static async Task<string> GenerateUniqueRecordingNameAsync(IMacroStore store)
    {
        var existing = await store.ListAsync(MacroScope.Global).ConfigureAwait(false);
        var rx = new Regex(@"^RecordedMacro(?<n>\d*)$");
        var maxN = -1;
        foreach (var item in existing)
        {
            var m = rx.Match(item.Name);
            if (!m.Success) continue;
            var nStr = m.Groups["n"].Value;
            var n = string.IsNullOrEmpty(nStr) ? 0 : int.Parse(nStr, CultureInfo.InvariantCulture);
            if (n > maxN) maxN = n;
        }
        return maxN < 0 ? "RecordedMacro1" : $"RecordedMacro{maxN + 1}";
    }
}
