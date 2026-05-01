using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine.Storage;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace Macros.Engine.Triggers;

/// <summary>
/// Default <see cref="IMacroTriggerRegistry"/>. Subscribes to <see cref="IMacroStore.LibraryChanged"/>
/// and rebuilds its indexes on a 100 ms debounce so a burst of file-system events (e.g. a
/// rename touches Removed + Added back-to-back) costs one store enumeration, not several.
/// </summary>
/// <remarks>
/// <para>
/// Construction is non-blocking: the initial <see cref="RefreshAsync"/> is queued through the
/// supplied <see cref="JoinableTaskFactory"/> as fire-and-forget so the package can wire the
/// registry from <c>InitializeAsync</c> without taking the cost of disk I/O on the UI thread.
/// Until that first refresh completes, <see cref="IsLoaded"/> reports <see langword="false"/>
/// and every query method returns an empty list (the dispatcher's hot-path checks therefore
/// behave as "no triggers registered yet" — exactly the right semantics for early VS startup).
/// </para>
/// <para>
/// The <c>disableAllTriggersProvider</c> hook lets the VSIX project funnel
/// <c>MacrosOptions.DisableAllTriggers</c> in without the engine taking a project reference
/// on the WPF/options layer. The provider is invoked on every query so the kill-switch goes
/// live the moment the user toggles it in Tools → Options — no reload needed, indexes stay
/// intact, queries simply short-circuit while the flag is on.
/// </para>
/// <para>
/// Threading: index swap happens under <c>_sync</c>; queries take the same lock for the
/// dictionary reads (snapshots are cheap — the lists themselves are immutable post-publish).
/// <see cref="Changed"/> fires after the lock is released, on whichever thread completed the
/// refresh — typically the threadpool. UI subscribers must marshal explicitly.
/// </para>
/// </remarks>
internal sealed class MacroTriggerRegistry : IMacroTriggerRegistry
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(100);
    private static readonly IReadOnlyList<TriggerMatch> EmptyMatches = Array.Empty<TriggerMatch>();

    private readonly IMacroStore _store;
    private readonly JoinableTaskFactory _jtf;
    private readonly Func<bool>? _disableAllTriggersProvider;

    private readonly object _sync = new();

    // Indexes are replaced wholesale on every refresh; lookups can therefore copy out the
    // reference under the lock and read it without further synchronization. OrdinalIgnoreCase
    // matches VS command and event naming conventions (case-insensitive in practice).
    private Dictionary<string, List<TriggerMatch>> _byEvent =
        new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<TriggerMatch>> _byBeforeCommand =
        new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<TriggerMatch>> _byAfterCommand =
        new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _hasBeforeCommandSet =
        new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _hasAfterCommandSet =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _isLoaded;
    private bool _disposed;

    // Debounce state for LibraryChanged-driven refreshes. _pendingDebounceCts cancels any
    // already-scheduled delay so a burst collapses into a single refresh fired 100 ms after
    // the last event in the burst.
    private CancellationTokenSource? _pendingDebounceCts;

    // Serializes overlapping RefreshAsync calls so concurrent callers share a single store
    // read instead of each kicking off their own enumeration. Callers always observe a
    // result that is at least as fresh as their request.
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    /// <summary>
    /// Initializes a new <see cref="MacroTriggerRegistry"/> over <paramref name="store"/>,
    /// subscribing to <see cref="IMacroStore.LibraryChanged"/> and queuing an initial refresh.
    /// </summary>
    /// <param name="store">The macro library to index. Required.</param>
    /// <param name="jtf">JoinableTaskFactory used to fire-and-forget the initial load.</param>
    /// <param name="disableAllTriggersProvider">
    /// Optional callback invoked on every query to read <c>MacrosOptions.DisableAllTriggers</c>.
    /// Production wiring lives in <c>MacrosPackage.InitializeAsync</c>:
    /// <c>() =&gt; MacrosOptions.Instance.DisableAllTriggers</c>. Tests pass <see langword="null"/>
    /// to keep the kill-switch off, or supply a captured-variable lambda to flip it on demand.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="store"/> or <paramref name="jtf"/> is <see langword="null"/>.
    /// </exception>
    public MacroTriggerRegistry(
        IMacroStore store,
        JoinableTaskFactory jtf,
        Func<bool>? disableAllTriggersProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _jtf = jtf ?? throw new ArgumentNullException(nameof(jtf));
        _disableAllTriggersProvider = disableAllTriggersProvider;

        _store.LibraryChanged += OnLibraryChanged;

        // Fire-and-forget the initial load through the JTF so the registry pre-populates on
        // package init without blocking the caller. Failures are surfaced via FileAndForget's
        // telemetry channel; queries gracefully return empty until the load completes.
        _jtf.RunAsync(async () =>
        {
            await TaskScheduler.Default;
            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }).FileAndForget("Macros/Triggers/Registry/InitialLoad");
    }

    /// <inheritdoc />
    public bool IsLoaded
    {
        get { lock (_sync) return _isLoaded; }
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public IReadOnlyList<TriggerMatch> FindByEvent(string canonicalEventName)
        => Lookup(canonicalEventName, byEvent: true, byBefore: false, byAfter: false);

    /// <inheritdoc />
    public IReadOnlyList<TriggerMatch> FindByBeforeCommand(string commandName)
        => Lookup(commandName, byEvent: false, byBefore: true, byAfter: false);

    /// <inheritdoc />
    public IReadOnlyList<TriggerMatch> FindByAfterCommand(string commandName)
        => Lookup(commandName, byEvent: false, byBefore: false, byAfter: true);

    /// <inheritdoc />
    public bool HasBeforeCommand(string commandName)
    {
        if (string.IsNullOrEmpty(commandName)) return false;
        if (IsKillSwitchActive()) return false;
        lock (_sync)
        {
            return _hasBeforeCommandSet.Contains(commandName);
        }
    }

    /// <inheritdoc />
    public bool HasAfterCommand(string commandName)
    {
        if (string.IsNullOrEmpty(commandName)) return false;
        if (IsKillSwitchActive()) return false;
        lock (_sync)
        {
            return _hasAfterCommandSet.Contains(commandName);
        }
    }

    private IReadOnlyList<TriggerMatch> Lookup(string key, bool byEvent, bool byBefore, bool byAfter)
    {
        if (string.IsNullOrEmpty(key)) return EmptyMatches;
        if (IsKillSwitchActive()) return EmptyMatches;

        lock (_sync)
        {
            Dictionary<string, List<TriggerMatch>> map;
            if (byEvent) map = _byEvent;
            else if (byBefore) map = _byBeforeCommand;
            else map = _byAfterCommand;

            if (!map.TryGetValue(key, out var list) || list.Count == 0)
            {
                return EmptyMatches;
            }

            // Hand back a defensive snapshot so callers can iterate without worrying about
            // a concurrent rebuild swapping the underlying list out from under them.
            return list.ToArray();
        }
    }

    /// <inheritdoc />
    public async Task RefreshAsync(CancellationToken cancellation = default)
    {
        // Take the gate so concurrent refresh requests serialize. The gate also doubles as
        // the disposal guard — a refresh entered before Dispose() will complete; one entered
        // after observes _disposed and bails out.
        await _refreshGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_disposed) return;
            }

            var entries = await _store.ListAllAsync(cancellation).ConfigureAwait(false);

            var byEvent = new Dictionary<string, List<TriggerMatch>>(StringComparer.OrdinalIgnoreCase);
            var byBefore = new Dictionary<string, List<TriggerMatch>>(StringComparer.OrdinalIgnoreCase);
            var byAfter = new Dictionary<string, List<TriggerMatch>>(StringComparer.OrdinalIgnoreCase);
            var hasBefore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hasAfter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (entries is not null)
            {
                foreach (var entry in entries)
                {
                    if (entry is null) continue;
                    foreach (var binding in entry.Triggers)
                    {
                        if (binding is null) continue;

                        var match = new TriggerMatch(entry, binding);
                        switch (binding.Kind)
                        {
                            case TriggerKind.VsEvent:
                                AddToIndex(byEvent, binding.Name, match);
                                break;
                            case TriggerKind.BeforeCommand:
                                AddToIndex(byBefore, binding.Name, match);
                                hasBefore.Add(binding.Name);
                                break;
                            case TriggerKind.AfterCommand:
                                AddToIndex(byAfter, binding.Name, match);
                                hasAfter.Add(binding.Name);
                                break;

                            // Manual bindings exist only so the tool window can render a
                            // "Manual" badge — they have no dispatch entry, so the registry
                            // intentionally drops them on the floor here.
                            case TriggerKind.Manual:
                            default:
                                break;
                        }
                    }
                }
            }

            lock (_sync)
            {
                _byEvent = byEvent;
                _byBeforeCommand = byBefore;
                _byAfterCommand = byAfter;
                _hasBeforeCommandSet = hasBefore;
                _hasAfterCommandSet = hasAfter;
                _isLoaded = true;
            }
        }
        finally
        {
            _refreshGate.Release();
        }

        // Fire outside the lock so handlers that re-enter the registry (e.g. a dispatcher
        // immediately looking up bindings) don't deadlock. The contract is that subscribers
        // marshal to the UI thread themselves.
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void AddToIndex(
        Dictionary<string, List<TriggerMatch>> map,
        string key,
        TriggerMatch match)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<TriggerMatch>(capacity: 1);
            map[key] = list;
        }
        list.Add(match);
    }

    private bool IsKillSwitchActive()
    {
        if (_disableAllTriggersProvider is null) return false;
        try
        {
            return _disableAllTriggersProvider();
        }
        catch
        {
            // A throwing options accessor must never break dispatch — fail closed (treat as
            // off) so manual operation continues unimpeded.
            return false;
        }
    }

    private void OnLibraryChanged(object? sender, MacroLibraryChangedEventArgs e)
    {
        ScheduleDebouncedRefresh();
    }

    private void ScheduleDebouncedRefresh()
    {
        CancellationTokenSource? previous;
        CancellationTokenSource fresh;

        lock (_sync)
        {
            if (_disposed) return;
            previous = _pendingDebounceCts;
            fresh = new CancellationTokenSource();
            _pendingDebounceCts = fresh;
        }

        // Cancel the in-flight delay (if any) so a burst collapses into one refresh fired
        // 100 ms after the LAST event, not 100 ms after the first.
        if (previous is not null)
        {
            try { previous.Cancel(); } catch (ObjectDisposedException) { }
            previous.Dispose();
        }

        _jtf.RunAsync(async () =>
        {
            await TaskScheduler.Default;
            try
            {
                await Task.Delay(DebounceDelay, fresh.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Clear the slot so the next event starts a new debounce window. We only clear
            // when the slot still holds OUR token — a later burst may have already moved on.
            lock (_sync)
            {
                if (ReferenceEquals(_pendingDebounceCts, fresh))
                {
                    _pendingDebounceCts = null;
                }
                if (_disposed) return;
            }

            try
            {
                await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Swallow — a failed refresh leaves the previous indexes in place. The
                // FileAndForget telemetry below surfaces the error.
            }
        }).FileAndForget("Macros/Triggers/Registry/DebouncedRefresh");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        CancellationTokenSource? pending;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            pending = _pendingDebounceCts;
            _pendingDebounceCts = null;
        }

        _store.LibraryChanged -= OnLibraryChanged;

        if (pending is not null)
        {
            try { pending.Cancel(); } catch (ObjectDisposedException) { }
            pending.Dispose();
        }

        _refreshGate.Dispose();
    }
}
