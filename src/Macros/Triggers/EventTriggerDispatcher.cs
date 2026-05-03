using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine;
using Macros.Engine.Player;
using Macros.Engine.Storage;
using Macros.Engine.Triggers;
using Macros.Lifecycle;
using Macros.Options;
using Macros.Trust;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace Macros.Triggers;

/// <summary>
/// Bridges the trigger pipeline's three independent components — the
/// <see cref="IMacroEventBus"/> (raw VS events), the <see cref="IMacroTriggerRegistry"/>
/// (which macros want which events), and the <see cref="IMacroPlayer"/> (compile + run) —
/// into a single live wiring so a <c>// @trigger Build.SolutionBuildDone</c> macro actually
/// fires when the underlying VS event raises.
/// </summary>
/// <remarks>
/// <para>
/// <b>Subscription model.</b> The dispatcher subscribes to the bus exactly once per
/// canonical event name that has at least one matching trigger in the registry. When the
/// registry rebuilds (a macro is added / removed / its triggers change), <see cref="OnRegistryChanged"/>
/// re-syncs the desired set: new names get a single shared <see cref="IMacroEventBus.Subscribe"/>
/// call, removed names have their subscription token disposed, and unchanged names keep
/// their existing subscription so listener-count reference counting stays stable inside the bus.
/// </para>
/// <para>
/// <b>Dispatch.</b> When the bus raises a <see cref="MacroEvent"/>, the listener queries the
/// registry for every <see cref="TriggerMatch"/> bound to that name and fires each macro on
/// the joinable task pool (fire-and-forget, identical to <c>AfterCommand</c> dispatch). VS
/// event triggers are by contract uncancellable — they are <em>after-the-fact</em> notifications
/// — so there is no synchronous return path and no equivalent of <c>Trigger.CancelCommand()</c>.
/// </para>
/// <para>
/// <b>Failure tracking.</b> Mirrors <see cref="CommandTriggerDispatcher"/>: each play reports
/// success / failure to the optional <see cref="IMacroFailureTracker"/> which auto-disables
/// macros after consecutive failures. The registry's <see cref="IMacroTriggerRegistry.FindByEvent"/>
/// already filters out auto-disabled paths, so the dispatcher needs no extra check.
/// </para>
/// <para>
/// <b>Threading.</b> Construction is non-blocking — the initial sync runs synchronously
/// against the registry snapshot (queries are lock-guarded). Bus listeners fire on whichever
/// thread the underlying VS event raised; the dispatcher hops to the joinable task pool
/// before invoking the player so a slow play never stalls the event source.
/// </para>
/// </remarks>
internal sealed class EventTriggerDispatcher : IDisposable
{
    private readonly IMacroTriggerRegistry _registry;
    private readonly IMacroEventBus _bus;
    private readonly IMacroPlayer _player;
    private readonly JoinableTaskFactory _jtf;
    private readonly IMacroFailureTracker? _tracker;
    private readonly Func<MacroEntry, string?> _sourceLoader;
    private readonly IMacroService? _macroService;
    private readonly Func<MacroEntry, CancellationToken, Task<bool>> _trustGate;

    private readonly object _sync = new();
    private readonly Dictionary<string, IDisposable> _subscriptions =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="EventTriggerDispatcher"/> and immediately syncs
    /// subscriptions against the current registry snapshot.
    /// </summary>
    /// <param name="registry">The trigger registry; consulted on every event firing and
    /// re-scanned on every <see cref="IMacroTriggerRegistry.Changed"/>.</param>
    /// <param name="bus">The macro event bus; the dispatcher owns one subscription per
    /// canonical event name with at least one registered trigger.</param>
    /// <param name="player">The compile-and-run pipeline shared with manual playback.</param>
    /// <param name="jtf">JoinableTaskFactory used to fire-and-forget background plays.</param>
    /// <param name="tracker">Optional consecutive-failure tracker. Pass <see langword="null"/>
    /// to opt out of auto-disable bookkeeping.</param>
    /// <param name="sourceLoader">Optional override for reading the macro source. Defaults
    /// to <see cref="File.ReadAllText(string)"/>; tests inject a fake.</param>
    /// <param name="macroService">Optional service handle for raising
    /// <c>TriggeredExecutionStarted</c> / <c>TriggeredExecutionEnded</c> so the status bar
    /// can mirror VS-event-driven runs the same way it does command-driven ones.</param>
    /// <param name="trustGate">Optional override for the per-entry trust check. When
    /// <see langword="null"/>, defaults to a pure <see cref="TrustGate.IsAllowed"/> check
    /// against <see cref="MacrosOptions.Instance"/> and the active solution path tracked by
    /// <see cref="SolutionContextTracker.Current"/>. Production wiring can supply a prompting
    /// callback; tests inject a deterministic async predicate.</param>
    public EventTriggerDispatcher(
        IMacroTriggerRegistry registry,
        IMacroEventBus bus,
        IMacroPlayer player,
        JoinableTaskFactory jtf,
        IMacroFailureTracker? tracker = null,
        Func<MacroEntry, string?>? sourceLoader = null,
        IMacroService? macroService = null,
        Func<MacroEntry, CancellationToken, Task<bool>>? trustGate = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _jtf = jtf ?? throw new ArgumentNullException(nameof(jtf));
        _tracker = tracker;
        _sourceLoader = sourceLoader ?? DefaultSourceLoader;
        _macroService = macroService;
        _trustGate = trustGate ?? DefaultTrustGateAsync;

        _registry.Changed += OnRegistryChanged;
        SyncSubscriptions();
    }

    private void OnRegistryChanged(object? sender, EventArgs e) => SyncSubscriptions();

    /// <summary>
    /// Reconciles the bus subscription set with the registry's current event-keyed index.
    /// Adds subscriptions for newly-bound names, drops subscriptions for names that no
    /// longer have any matches, and leaves the rest untouched so the bus's lazy-attach
    /// reference counting stays stable.
    /// </summary>
    private void SyncSubscriptions()
    {
        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var known in _bus.GetKnownEvents())
        {
            if (_registry.FindByEvent(known.CanonicalName).Count > 0)
            {
                desired.Add(known.CanonicalName);
            }
        }

        List<IDisposable>? toDisposeOutsideLock = null;
        List<string>? toAdd = null;

        lock (_sync)
        {
            if (_disposed) return;

            // Drop subscriptions no longer wanted.
            var stale = _subscriptions.Keys.Where(k => !desired.Contains(k)).ToArray();
            foreach (var name in stale)
            {
                (toDisposeOutsideLock ??= new List<IDisposable>()).Add(_subscriptions[name]);
                _subscriptions.Remove(name);
            }

            // Schedule additions; perform Subscribe outside the lock so a synchronous
            // attach failure inside the bus can't deadlock against a concurrent re-sync.
            foreach (var name in desired)
            {
                if (!_subscriptions.ContainsKey(name))
                {
                    (toAdd ??= new List<string>()).Add(name);
                }
            }
        }

        if (toDisposeOutsideLock != null)
        {
            foreach (var d in toDisposeOutsideLock)
            {
                try { d.Dispose(); } catch { /* unsubscribe failure must not abort sync */ }
            }
        }

        if (toAdd != null)
        {
            foreach (var name in toAdd)
            {
                IDisposable? sub = null;
                try { sub = _bus.Subscribe(name, OnEvent); }
                catch { /* a throwing Subscribe must not abort sync */ }

                if (sub == null) continue;

                lock (_sync)
                {
                    if (_disposed)
                    {
                        try { sub.Dispose(); } catch { }
                        return;
                    }

                    if (!_subscriptions.ContainsKey(name))
                    {
                        _subscriptions[name] = sub;
                    }
                    else
                    {
                        // Lost a race with another sync — drop the redundant token.
                        try { sub.Dispose(); } catch { }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Bus callback. Looks up every macro bound to <paramref name="evt"/>'s canonical name
    /// and queues each on the joinable task pool. VS event triggers are uncancellable, so
    /// the bus continues onward regardless of the play outcome.
    /// </summary>
    private void OnEvent(MacroEvent evt)
    {
        var matches = _registry.FindByEvent(evt.CanonicalName);
        if (matches.Count == 0) return;

        foreach (var match in matches)
        {
            var entry = match.Entry;
            var canonicalName = evt.CanonicalName;
            var firedAt = evt.FiredAt;
            var payload = evt.Payload;

            _jtf.RunAsync(async () =>
            {
                try
                {
                    bool allowed;
                    try { allowed = await _trustGate(entry, CancellationToken.None).ConfigureAwait(true); }
                    catch { allowed = false; }
                    if (!allowed) return;

                    var source = _sourceLoader(entry);
                    if (source is null)
                    {
                        _tracker?.RecordFailure(entry.Path);
                        return;
                    }

                    var trigger = new VsEventMacroTrigger(canonicalName, firedAt, payload);
                    var triggeredArgs = new TriggeredExecutionEventArgs(entry.Name, canonicalName, TriggerKind.VsEvent);

                    if (_macroService is MacroService svc)
                    {
                        svc.RaiseTriggeredStarted(triggeredArgs);
                    }

                    try
                    {
                        var result = await _player.PlayAsync(source, entry.Name, trigger, CancellationToken.None, entry.Path).ConfigureAwait(true);
                        if (result.Success)
                        {
                            _tracker?.RecordSuccess(entry.Path);
                        }
                        else
                        {
                            _tracker?.RecordFailure(entry.Path);
                        }
                    }
                    finally
                    {
                        if (_macroService is MacroService svc2)
                        {
                            svc2.RaiseTriggeredEnded(triggeredArgs);
                        }
                    }
                }
                catch (Exception)
                {
                    _tracker?.RecordFailure(entry.Path);
                }
            }).FileAndForget("Macros/EventTrigger");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        IDisposable[] toDispose;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            toDispose = _subscriptions.Values.ToArray();
            _subscriptions.Clear();
        }

        _registry.Changed -= OnRegistryChanged;

        foreach (var sub in toDispose)
        {
            try { sub.Dispose(); } catch { /* shutdown */ }
        }
    }

    private static string? DefaultSourceLoader(MacroEntry entry)
    {
        try { return File.ReadAllText(entry.Path); }
        catch { return null; }
    }

    private static Task<bool> DefaultTrustGateAsync(MacroEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            var soln = SolutionContextTracker.Current?.GetCurrentSolutionPath();
            return Task.FromResult(TrustGate.IsAllowed(entry, MacrosOptions.Instance, soln));
        }
        catch
        {
            // Fail-safe: only Global entries when the static state isn't reachable.
            return Task.FromResult(entry?.Scope == MacroScope.Global);
        }
    }
}
