using System;

namespace Macros.Engine.Triggers;

/// <summary>
/// Metadata describing a single VS event discovered on
/// <c>Community.VisualStudio.Toolkit.VS.Events</c> by <see cref="KnownEvents"/>.
/// </summary>
/// <remarks>
/// <para>
/// One <see cref="KnownVsEvent"/> exists per public instance event on a category
/// (<c>BuildEvents</c>, <c>DocumentEvents</c>, <c>SolutionEvents</c>, …). The flattened
/// <see cref="CanonicalName"/> form (<c>"{Category}.{EventName}"</c>) is the user-facing
/// trigger name a macro writes in its <c>// @trigger ...</c> directive — see
/// <c>danny-triggers-and-scope.md §2.2</c>.
/// </para>
/// <para>
/// <see cref="EventArgsType"/> is the resolved argument type of the underlying delegate
/// (<c>EventHandler&lt;T&gt;</c> → <c>T</c>; plain <c>EventHandler</c> / <c>Action</c> →
/// <see cref="EventArgs"/>). The downstream <c>MacroEventBus</c> uses it to construct the
/// strongly-typed handler delegate at subscription time.
/// </para>
/// </remarks>
public sealed record KnownVsEvent(
    string CanonicalName,
    string Category,
    string EventName,
    Type EventArgsType,
    Type DeclaringType);
