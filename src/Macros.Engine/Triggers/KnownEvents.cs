using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Macros.Engine.Triggers;

/// <summary>
/// Reflection-based catalog of every VS event exposed by
/// <c>Community.VisualStudio.Toolkit.VS.Events</c>. The catalog is built lazily on first
/// access and cached for the lifetime of the AppDomain.
/// </summary>
/// <remarks>
/// <para>
/// Discovery walks the toolkit's <c>VS.Events</c> static surface:
/// </para>
/// <list type="number">
///   <item>Locate <c>Community.VisualStudio.Toolkit.VS</c> via its assembly-qualified name.</item>
///   <item>Read the static <c>Events</c> property to obtain the singleton <c>Events</c> instance.</item>
///   <item>For each instance property on that object (<c>BuildEvents</c>, <c>DocumentEvents</c>,
///         …), enumerate its public instance events.</item>
///   <item>Emit one <see cref="KnownVsEvent"/> per event with the flattened
///         <c>"{Category}.{EventName}"</c> canonical name.</item>
/// </list>
/// <para>
/// Toolkit absence is non-fatal: if any reflection step throws or returns null (e.g. the
/// host process never loaded <c>Community.VisualStudio.Toolkit</c> — common in unit-test
/// runs), <see cref="All"/> resolves to an empty list rather than propagating the failure.
/// Callers — including <see cref="Find"/> — must tolerate that. The downstream
/// <c>MacroEventBus</c> is expected to log loudly when the catalog is empty inside Visual
/// Studio (see <c>danny-triggers-and-scope.md §2.3</c>).
/// </para>
/// </remarks>
public static class KnownEvents
{
    private static readonly Lazy<IReadOnlyList<KnownVsEvent>> _all =
        new(Discover, isThreadSafe: true);

    /// <summary>Every event discovered on <c>VS.Events</c>, or an empty list if the toolkit isn't loaded.</summary>
    public static IReadOnlyList<KnownVsEvent> All => _all.Value;

    /// <summary>
    /// Looks up an event by its flattened canonical name (<c>"Build.SolutionBuildDone"</c>).
    /// Comparison is case-insensitive. Returns <c>null</c> when no match exists.
    /// </summary>
    public static KnownVsEvent? Find(string canonicalName) =>
        _all.Value.FirstOrDefault(
            e => string.Equals(e.CanonicalName, canonicalName, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<KnownVsEvent> Discover()
    {
        var list = new List<KnownVsEvent>();
        try
        {
            var vsType = Type.GetType(
                "Community.VisualStudio.Toolkit.VS, Community.VisualStudio.Toolkit");
            if (vsType == null)
            {
                return list.AsReadOnly();
            }

            var eventsProp = vsType.GetProperty("Events", BindingFlags.Public | BindingFlags.Static);
            if (eventsProp == null)
            {
                return list.AsReadOnly();
            }

            var eventsInstance = eventsProp.GetValue(null);
            if (eventsInstance == null)
            {
                return list.AsReadOnly();
            }

            foreach (var catProp in eventsInstance.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                object? catInstance;
                try
                {
                    catInstance = catProp.GetValue(eventsInstance);
                }
                catch
                {
                    // A category that throws on access is unusable; skip it rather than
                    // failing the whole discovery pass.
                    continue;
                }

                if (catInstance == null)
                {
                    continue;
                }

                var category = StripEventsSuffix(catProp.Name);
                var catType = catInstance.GetType();

                foreach (var ev in catType.GetEvents(BindingFlags.Public | BindingFlags.Instance))
                {
                    var argsType = ResolveEventArgsType(ev.EventHandlerType);
                    list.Add(new KnownVsEvent(
                        CanonicalName: $"{category}.{ev.Name}",
                        Category: category,
                        EventName: ev.Name,
                        EventArgsType: argsType,
                        DeclaringType: catType));
                }
            }
        }
        catch
        {
            // Swallow — see remarks on KnownEvents: an empty catalog is the documented
            // signal that the toolkit isn't available in this process.
            return list.AsReadOnly();
        }

        return list.AsReadOnly();
    }

    private static string StripEventsSuffix(string s) =>
        s.EndsWith("Events", StringComparison.Ordinal)
            ? s.Substring(0, s.Length - "Events".Length)
            : s;

    private static Type ResolveEventArgsType(Type? handlerType)
    {
        if (handlerType == null)
        {
            return typeof(EventArgs);
        }

        // EventHandler<T> → T; plain Action / EventHandler → EventArgs.
        if (handlerType.IsGenericType
            && handlerType.GetGenericTypeDefinition() == typeof(EventHandler<>))
        {
            return handlerType.GetGenericArguments()[0];
        }

        return typeof(EventArgs);
    }
}
