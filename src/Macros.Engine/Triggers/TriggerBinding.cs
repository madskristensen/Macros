using System;
using System.Collections.Generic;
using System.Linq;

namespace Macros.Engine.Triggers;

/// <summary>
/// One parsed <c>@trigger</c> directive from a macro's leading comment block.
/// </summary>
/// <remarks>
/// <para>
/// A binding is the engine-side data model for a single line such as
/// <c>// @trigger Document.Saved when filename=*.cs</c>. The text is parsed by
/// <see cref="TriggerDirectiveParser"/>; later milestones (M4 trigger registry, event bus,
/// command dispatcher) consume the bindings to wire macros to runtime events.
/// </para>
/// <para>
/// Equality is structural: two bindings are equal iff <see cref="Kind"/>, <see cref="Name"/>,
/// and <see cref="Filters"/> contents all match. The compiler-synthesized
/// <see cref="object.Equals(object?)"/> for <c>record</c> uses reference equality on
/// <see cref="IReadOnlyDictionary{TKey,TValue}"/>, which would defeat round-trip and
/// list-comparison tests, so both <see cref="Equals(TriggerBinding?)"/> and
/// <see cref="GetHashCode"/> are overridden to do the right thing.
/// </para>
/// </remarks>
public sealed record TriggerBinding
{
    /// <summary>The directive form (Manual, VsEvent, BeforeCommand, AfterCommand).</summary>
    public TriggerKind Kind { get; }

    /// <summary>
    /// The directive payload: the literal <c>"Manual"</c>, a fully-qualified VS event name
    /// (<c>"Build.SolutionBuildDone"</c>), or a command name (<c>"File.Save"</c>).
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Optional <c>when key=value [and key=value...]</c> filters. Empty for command-trigger
    /// and Manual bindings; populated for VsEvent bindings that included a <c>when</c> clause.
    /// Keys are compared case-insensitively.
    /// </summary>
    public IReadOnlyDictionary<string, string> Filters { get; }

    public TriggerBinding(
        TriggerKind kind,
        string name,
        IReadOnlyDictionary<string, string>? filters = null)
    {
        Kind = kind;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Filters = filters ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The default binding when a macro declares no <c>@trigger</c> directives.</summary>
    public static readonly TriggerBinding Manual = new(TriggerKind.Manual, "Manual");

    public override int GetHashCode()
    {
        // System.HashCode is not exposed on net48; use a manual FNV-style combine.
        // Filters.Count (not contents) is sufficient as a hash mixer — Equals does the
        // structural compare, and the spec only requires hash consistency with equality.
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + Kind.GetHashCode();
            hash = hash * 31 + Name.GetHashCode();
            hash = hash * 31 + Filters.Count;
            return hash;
        }
    }

    public bool Equals(TriggerBinding? other) =>
        other is not null
        && Kind == other.Kind
        && Name == other.Name
        && Filters.Count == other.Filters.Count
        && Filters.All(kv => other.Filters.TryGetValue(kv.Key, out var v) && v == kv.Value);
}
