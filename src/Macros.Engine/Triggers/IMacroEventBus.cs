using System;
using System.Collections.Generic;

namespace Macros.Engine.Triggers;

/// <summary>
/// A single firing of a VS event delivered to macro trigger listeners.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Payload"/> dictionary carries the event-args fields flattened to a
/// string-keyed bag so that <c>when</c>-clause filters in the <c>// @trigger</c> directive
/// (e.g. <c>filename=*.cs</c>) can be evaluated without the engine knowing the concrete
/// <c>EventArgs</c> shape. Population is the responsibility of <c>MacroEventBus</c>; an
/// empty payload is allowed for events whose args type is plain <see cref="EventArgs"/>.
/// </para>
/// </remarks>
public sealed class MacroEvent
{
    /// <summary>The flattened <c>"{Category}.{EventName}"</c> form, e.g. <c>"Build.SolutionBuildDone"</c>.</summary>
    public string CanonicalName { get; }

    /// <summary>UTC timestamp (with offset) at which the underlying VS event fired.</summary>
    public DateTimeOffset FiredAt { get; }

    /// <summary>Structured event-arg fields. Keys are case-sensitive at this layer; filter matching is case-insensitive elsewhere.</summary>
    public IReadOnlyDictionary<string, object?> Payload { get; }

    public MacroEvent(
        string canonicalName,
        DateTimeOffset firedAt,
        IReadOnlyDictionary<string, object?> payload)
    {
        CanonicalName = canonicalName ?? throw new ArgumentNullException(nameof(canonicalName));
        FiredAt = firedAt;
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
    }
}

/// <summary>
/// Subscription manager that bridges <c>Community.VisualStudio.Toolkit.VS.Events</c> handlers
/// to macro trigger listeners.
/// </summary>
/// <remarks>
/// <para>
/// Implementations (see <c>MacroEventBus</c>) are expected to subscribe to each underlying
/// VS event lazily — only when at least one listener exists for that event — and to keep the
/// subscription alive via reference counting so that the last <see cref="IDisposable.Dispose"/>
/// on a returned subscription token unsubscribes from VS as well.
/// </para>
/// <para>
/// The bus itself is <see cref="IDisposable"/> so the host can deterministically tear down
/// every active VS subscription on package unload, regardless of whether individual listener
/// tokens were disposed.
/// </para>
/// </remarks>
public interface IMacroEventBus : IDisposable
{
    /// <summary>
    /// Subscribes <paramref name="handler"/> to the named event. The returned
    /// <see cref="IDisposable"/> unsubscribes when disposed; disposing twice is a no-op.
    /// </summary>
    /// <param name="canonicalName">Flattened name from <see cref="KnownVsEvent.CanonicalName"/>.</param>
    /// <param name="handler">Callback invoked synchronously by the bus for every firing.</param>
    /// <remarks>
    /// Lazily activates the underlying VS event on the first subscriber. The event remains
    /// subscribed to VS until the last listener token is disposed.
    /// </remarks>
    IDisposable Subscribe(string canonicalName, Action<MacroEvent> handler);

    /// <summary>Returns every event discovered by <see cref="KnownEvents"/>.</summary>
    IReadOnlyList<KnownVsEvent> GetKnownEvents();
}
