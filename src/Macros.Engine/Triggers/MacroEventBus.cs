using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;

namespace Macros.Engine.Triggers;

/// <summary>
/// Default <see cref="IMacroEventBus"/>: bridges <c>Community.VisualStudio.Toolkit.VS.Events</c>
/// firings into the macro trigger pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lazy-subscribe lifecycle.</b> The bus does <i>not</i> attach handlers to any VS event at
/// construction. The first <see cref="Subscribe"/> call for a given canonical name allocates a
/// <see cref="BusEntry"/>, builds an event-handler delegate matching the underlying event's
/// signature, and calls <c>AddEventHandler</c> on the toolkit's category instance. Each
/// subsequent <see cref="Subscribe"/> for the same name only appends to the listener list —
/// the VS-side subscription is never doubled. When the last subscription token for a name is
/// disposed, the listener is removed and the cached delegate is detached via
/// <c>RemoveEventHandler</c>; the entry stays in <see cref="_entries"/> so a future subscriber
/// can re-attach without re-resolving reflection metadata.
/// </para>
/// <para>
/// <b>Unknown events.</b> Subscribing to a name that is not in the
/// <see cref="KnownEvents"/> catalog (typo, toolkit not loaded, …) is intentionally non-fatal:
/// a no-op <see cref="IDisposable"/> is returned and the situation is traced via
/// <see cref="Debug.WriteLine(string)"/>. This favours DX inside Visual Studio over hard
/// failures during macro registration.
/// </para>
/// <para>
/// <b>Failure isolation.</b> A listener that throws during dispatch does not abort the rest
/// of the listeners and does not poison the underlying VS subscription. Exceptions are caught
/// and logged via <see cref="Debug.WriteLine(string)"/>. Tracing to the VS Output pane is the
/// VSIX project's responsibility.
/// </para>
/// </remarks>
internal sealed class MacroEventBus : IMacroEventBus
{
    private readonly Dictionary<string, BusEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _sync = new();
    private readonly IReadOnlyList<KnownVsEvent> _knownEvents;
    private readonly Func<Type, object?> _instanceResolver;
    private readonly TriggerWorkQueue? _queue;
    private readonly Func<bool>? _isDisabledProvider;
    private readonly TriggerReentranceGuard? _reentranceGuard;

    private bool _disposed;

    /// <summary>
    /// Production constructor: discovers events via the toolkit catalog.
    /// Pass a <see cref="TriggerWorkQueue"/> to dispatch listeners serially via the queue;
    /// omit (or pass <see langword="null"/>) for the original synchronous in-loop behaviour.
    /// </summary>
    /// <param name="queue">Optional work queue for serial dispatch.</param>
    /// <param name="isDisabledProvider">
    /// Optional kill-switch callback. When it returns <see langword="true"/> the bus silently
    /// drops the VS event — listeners are not invoked. Production wiring:
    /// <c>() =&gt; MacrosOptions.Instance.DisableAllTriggers</c>. Pass
    /// <see langword="null"/> (default) to always deliver events.
    /// </param>
    /// <param name="guard">Optional reentrance guard. When supplied, a listener re-firing the
    /// same event (or depth-exceeding recursive chains) is suppressed silently.</param>
    public MacroEventBus(TriggerWorkQueue? queue = null, Func<bool>? isDisabledProvider = null, TriggerReentranceGuard? guard = null)
        : this(KnownEvents.All, DefaultResolveCategoryInstance, queue, isDisabledProvider, guard)
    {
    }

    /// <summary>
    /// Test-only constructor allowing a hand-rolled known-events list and category-instance
    /// resolver, so tests can exercise the lazy-subscribe / refcount / dispatch path against a
    /// fake event surface without dragging in <c>Community.VisualStudio.Toolkit</c>.
    /// </summary>
    internal MacroEventBus(
        IReadOnlyList<KnownVsEvent> knownEvents,
        Func<Type, object?> instanceResolver,
        TriggerWorkQueue? queue = null,
        Func<bool>? isDisabledProvider = null,
        TriggerReentranceGuard? guard = null)
    {
        _knownEvents = knownEvents ?? throw new ArgumentNullException(nameof(knownEvents));
        _instanceResolver = instanceResolver ?? throw new ArgumentNullException(nameof(instanceResolver));
        _queue = queue;
        _isDisabledProvider = isDisabledProvider;
        _reentranceGuard = guard;
    }

    public IReadOnlyList<KnownVsEvent> GetKnownEvents() => _knownEvents;

    public IDisposable Subscribe(string canonicalName, Action<MacroEvent> handler)
    {
        if (canonicalName == null) throw new ArgumentNullException(nameof(canonicalName));
        if (handler == null) throw new ArgumentNullException(nameof(handler));

        BusEntry entry;
        bool needAttach = false;

        lock (_sync)
        {
            if (_disposed)
            {
                Debug.WriteLine($"Macros bus: Subscribe('{canonicalName}') after Dispose; returning no-op.");
                return NoOpDisposable.Instance;
            }

            if (!_entries.TryGetValue(canonicalName, out var existing))
            {
                var known = _knownEvents.FirstOrDefault(e =>
                    string.Equals(e.CanonicalName, canonicalName, StringComparison.OrdinalIgnoreCase));
                if (known == null)
                {
                    Debug.WriteLine($"Macros bus: unknown event '{canonicalName}'; returning no-op subscription.");
                    return NoOpDisposable.Instance;
                }

                existing = new BusEntry(known);
                _entries[known.CanonicalName] = existing;
            }

            entry = existing;
            entry.Listeners.Add(handler);
            if (entry.Listeners.Count == 1)
            {
                needAttach = true;
            }
        }

        if (needAttach)
        {
            try
            {
                AttachHandler(entry);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Macros bus: AttachHandler failed for '{canonicalName}': {ex}");
            }
        }

        return new Subscription(this, entry, handler);
    }

    public void Dispose()
    {
        BusEntry[] toDetach;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            toDetach = _entries.Values.Where(e => e.AttachedHandler != null).ToArray();
            foreach (var e in _entries.Values)
            {
                e.Listeners.Clear();
            }
        }

        foreach (var entry in toDetach)
        {
            try
            {
                DetachHandler(entry);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Macros bus: DetachHandler failed for '{entry.KnownEvent.CanonicalName}' on Dispose: {ex}");
            }
        }

        lock (_sync)
        {
            _entries.Clear();
        }
    }

    private void Unsubscribe(BusEntry entry, Action<MacroEvent> handler)
    {
        bool needDetach = false;
        lock (_sync)
        {
            if (_disposed) return;
            if (!entry.Listeners.Remove(handler)) return;
            if (entry.Listeners.Count == 0 && entry.AttachedHandler != null)
            {
                needDetach = true;
            }
        }

        if (needDetach)
        {
            try
            {
                DetachHandler(entry);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Macros bus: DetachHandler failed for '{entry.KnownEvent.CanonicalName}': {ex}");
            }
        }
    }

    private void AttachHandler(BusEntry entry)
    {
        var ev = entry.KnownEvent.DeclaringType.GetEvent(
            entry.KnownEvent.EventName,
            BindingFlags.Public | BindingFlags.Instance);
        if (ev == null || ev.EventHandlerType == null) return;

        var instance = _instanceResolver(entry.KnownEvent.DeclaringType);
        if (instance == null)
        {
            Debug.WriteLine($"Macros bus: no instance resolved for category '{entry.KnownEvent.DeclaringType.FullName}'.");
            return;
        }

        Action<object?> raiser = args => RaiseListeners(entry, args);
        var del = BuildDelegate(ev.EventHandlerType, raiser);
        ev.AddEventHandler(instance, del);
        entry.AttachedHandler = del;
    }

    private void DetachHandler(BusEntry entry)
    {
        if (entry.AttachedHandler == null) return;
        var ev = entry.KnownEvent.DeclaringType.GetEvent(
            entry.KnownEvent.EventName,
            BindingFlags.Public | BindingFlags.Instance);
        var instance = _instanceResolver(entry.KnownEvent.DeclaringType);
        if (ev != null && instance != null)
        {
            ev.RemoveEventHandler(instance, entry.AttachedHandler);
        }
        entry.AttachedHandler = null;
    }

    private void RaiseListeners(BusEntry entry, object? eventArgs)
    {
        // Kill switch: if triggers are globally disabled, drop the event silently.
        if (_isDisabledProvider?.Invoke() == true)
            return;

        var payload = ExtractPayload(eventArgs);
        var evt = new MacroEvent(entry.KnownEvent.CanonicalName, DateTimeOffset.UtcNow, payload);

        Action<MacroEvent>[] snapshot;
        lock (_sync)
        {
            snapshot = entry.Listeners.ToArray();
        }

        foreach (var listener in snapshot)
        {
            var l = listener;
            var canonicalName = entry.KnownEvent.CanonicalName;
            var key = $"event:{canonicalName}";
            if (_queue != null)
            {
                var guard = _reentranceGuard;
                _ = _queue.EnqueueAsync(() =>
                {
                    IDisposable? scope = null;
                    if (guard != null && !guard.TryEnter(key, out scope))
                    {
                        return Task.CompletedTask; // suppressed — recursive firing or depth exceeded
                    }

                    using (scope)
                    {
                        try { l(evt); }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Macros bus: queued listener for '{canonicalName}' threw: {ex}");
                        }
                    }
                    return Task.CompletedTask;
                });
            }
            else
            {
                IDisposable? scope = null;
                if (_reentranceGuard != null && !_reentranceGuard.TryEnter(key, out scope))
                {
                    continue; // suppressed — recursive firing or depth exceeded
                }

                using (scope)
                {
                    try
                    {
                        l(evt);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Macros bus: listener for '{canonicalName}' threw: {ex}");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Flattens an event-args object into a case-insensitive property dictionary. Used by
    /// <see cref="RaiseListeners"/> to populate <see cref="MacroEvent.Payload"/>; exposed at
    /// <c>internal</c> visibility so tests can pin the contract directly.
    /// </summary>
    internal static IReadOnlyDictionary<string, object?> ExtractPayload(object? eventArgs)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (eventArgs == null) return dict;

        foreach (var prop in eventArgs.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead) continue;
            if (prop.GetIndexParameters().Length > 0) continue;
            try
            {
                dict[prop.Name] = prop.GetValue(eventArgs);
            }
            catch
            {
                // A property whose getter throws is not worth aborting the whole payload over.
            }
        }
        return dict;
    }

    // Per-EventHandlerType factory cache.
    // Key:   the concrete delegate type (e.g. EventHandler<BuildDoneEventArgs>)
    // Value: a compiled factory Func<Action<object?>, Delegate> that wraps a given raiser in
    //        a strongly-typed delegate matching eventHandlerType.
    //
    // Caching the factory (not the delegate) is the critical distinction: the factory is
    // compiled once via Expression.Lambda.Compile() which emits and JIT-compiles a new
    // DynamicMethod (~1–10 ms); subsequent calls to the factory only allocate a closure
    // (~1 µs), so P95 first-subscribe time drops from ~10 ms to <0.5 ms.
    private static readonly ConcurrentDictionary<Type, Func<Action<object?>, Delegate>>
        _delegateFactoryCache = new();

    /// <summary>
    /// Returns a delegate of <paramref name="eventHandlerType"/> that forwards the event-args
    /// (always the last delegate parameter — covers <c>EventHandler</c>,
    /// <c>EventHandler&lt;T&gt;</c>, and any other <c>(sender, args)</c> shape) to
    /// <paramref name="raiser"/>. Zero-parameter delegates fire the raiser with <c>null</c>.
    /// </summary>
    /// <remarks>
    /// A per-<paramref name="eventHandlerType"/> factory delegate is compiled exactly once via
    /// <see cref="Expression"/> trees and cached in <see cref="_delegateFactoryCache"/>. The
    /// factory is then called with <paramref name="raiser"/> to produce the final delegate,
    /// avoiding a fresh <c>Expression.Lambda.Compile()</c> (and its DynamicMethod JIT cost)
    /// on every <see cref="Subscribe"/> call.
    /// </remarks>
    private static Delegate BuildDelegate(Type eventHandlerType, Action<object?> raiser)
    {
        var factory = _delegateFactoryCache.GetOrAdd(eventHandlerType, CompileDelegateFactory);
        return factory(raiser);
    }

    /// <summary>
    /// Compiles a factory for <paramref name="eventHandlerType"/>: a function that, given an
    /// <c>Action&lt;object?&gt;</c> raiser, creates a new delegate of
    /// <paramref name="eventHandlerType"/> forwarding its last argument (the event args) to the
    /// raiser. Called at most once per distinct event handler type.
    /// </summary>
    private static Func<Action<object?>, Delegate> CompileDelegateFactory(Type eventHandlerType)
    {
        var invoke = eventHandlerType.GetMethod("Invoke")
            ?? throw new InvalidOperationException(
                $"Delegate type '{eventHandlerType.FullName}' has no Invoke method.");

        var paramz = invoke.GetParameters();
        // Outer parameter: the raiser supplied at subscription time.
        var raiserParam = Expression.Parameter(typeof(Action<object?>), "raiser");

        LambdaExpression handlerLambda;
        if (paramz.Length == 0)
        {
            var nullArg = Expression.Constant(null, typeof(object));
            var raiseCall = Expression.Invoke(raiserParam, nullArg);
            handlerLambda = Expression.Lambda(eventHandlerType, raiseCall);
        }
        else
        {
            var dynParams = paramz
                .Select(p => Expression.Parameter(p.ParameterType, p.Name))
                .ToArray();
            var argsParam = dynParams[dynParams.Length - 1];
            var argsAsObject = Expression.Convert(argsParam, typeof(object));
            var raiseCall = Expression.Invoke(raiserParam, argsAsObject);
            handlerLambda = Expression.Lambda(eventHandlerType, raiseCall, dynParams);
        }

        // Factory lambda: (Action<object?> raiser) => (TEventHandler)handlerLambda
        // Compiling this once gives a factory whose invocation only allocates a closure.
        var factoryExpr = Expression.Lambda<Func<Action<object?>, Delegate>>(
            Expression.Convert(handlerLambda, typeof(Delegate)),
            raiserParam);
        return factoryExpr.Compile();
    }

    /// <summary>
    /// Default resolver: walks <c>Community.VisualStudio.Toolkit.VS.Events</c> and returns the
    /// category instance whose runtime type matches <paramref name="categoryType"/>. Returns
    /// <see langword="null"/> when the toolkit is not loaded — matches the empty-catalog
    /// behaviour of <see cref="KnownEvents"/>.
    /// </summary>
    private static object? DefaultResolveCategoryInstance(Type categoryType)
    {
        var vsType = Type.GetType("Community.VisualStudio.Toolkit.VS, Community.VisualStudio.Toolkit");
        if (vsType == null) return null;

        var eventsProp = vsType.GetProperty("Events", BindingFlags.Public | BindingFlags.Static);
        var events = eventsProp?.GetValue(null);
        if (events == null) return null;

        foreach (var p in events.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            object? candidate;
            try { candidate = p.GetValue(events); }
            catch { continue; }
            if (candidate != null && categoryType.IsInstanceOfType(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// Per-event state: which <see cref="KnownVsEvent"/> the entry represents, the listener
    /// list (also acts as the refcount), and the strongly-typed delegate currently attached
    /// to the underlying VS event (<see langword="null"/> when the entry has no listeners).
    /// </summary>
    private sealed class BusEntry
    {
        public KnownVsEvent KnownEvent { get; }
        public List<Action<MacroEvent>> Listeners { get; } = new();
        public Delegate? AttachedHandler { get; set; }

        public BusEntry(KnownVsEvent ev) => KnownEvent = ev;
    }

    private sealed class Subscription : IDisposable
    {
        private MacroEventBus? _owner;
        private readonly BusEntry _entry;
        private readonly Action<MacroEvent> _handler;

        public Subscription(MacroEventBus owner, BusEntry entry, Action<MacroEvent> handler)
        {
            _owner = owner;
            _entry = entry;
            _handler = handler;
        }

        public void Dispose()
        {
            var owner = System.Threading.Interlocked.Exchange(ref _owner, null);
            owner?.Unsubscribe(_entry, _handler);
        }
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public static readonly NoOpDisposable Instance = new();
        private NoOpDisposable() { }
        public void Dispose() { }
    }
}
