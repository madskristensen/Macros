using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Macros.Engine.Triggers;

[assembly: InternalsVisibleTo("Macros.Tests")]

namespace Macros.Engine.Scripting;

/// <summary>
/// Read-only metadata about the current macro invocation that user-authored <c>.csx</c> code can
/// introspect via the script <c>Globals.Context</c> property.
/// </summary>
/// <remarks>
/// <para>
/// The contract is intentionally narrow: the engine populates these values once before the script
/// runs and never mutates them mid-execution. User code therefore observes a stable snapshot.
/// </para>
/// <para>
/// In M2 the only producer is the manual <c>Play</c> command. M4 will introduce trigger-driven
/// playback (build events, before/after command, etc.) — those producers will populate
/// <see cref="TriggerKind"/> and the <see cref="Trigger"/> bag with trigger-specific keys.
/// </para>
/// </remarks>
public interface IMacroContext
{
    /// <summary>Gets the logical name of the macro being played (without the <c>.csx</c> extension).</summary>
    string MacroName { get; }

    /// <summary>
    /// Gets the kind of trigger that initiated playback. Stable string values such as
    /// <c>"Manual"</c>, <c>"BuildSucceeded"</c>, <c>"BeforeCommand"</c>. Used for diagnostics and
    /// for user-facing branching (<c>if (Context.TriggerKind == "BuildSucceeded") ...</c>).
    /// </summary>
    string TriggerKind { get; }

    /// <summary>
    /// Gets the typed trigger that initiated this playback. Always non-null; use
    /// <see cref="IMacroTrigger.IsManual"/> to distinguish user-initiated runs.
    /// </summary>
    IMacroTrigger Trigger { get; }

    /// <summary>
    /// Gets a value indicating whether the macro was started by direct user action
    /// (the <c>Play</c> command) rather than by an automated trigger.
    /// </summary>
    bool IsManual { get; }

    /// <summary>
    /// Gets the cancellation token observed by the macro player. User code that performs
    /// long-running work should pass this token into helper calls / <c>Task.Delay</c> / etc.
    /// </summary>
    CancellationToken Cancellation { get; }
}

/// <summary>
/// Default <see cref="IMacroContext"/> implementation built by the macro player and surfaced to
/// scripts as <c>Globals.Context</c>.
/// </summary>
/// <remarks>
/// Marked <see langword="internal"/> because production callers should depend on
/// <see cref="IMacroContext"/>; the unit-test assembly is granted access via
/// <see cref="InternalsVisibleToAttribute"/> so contract tests can exercise the constructor.
/// </remarks>
internal sealed class MacroContext : IMacroContext
{
    /// <summary>The string used by manual <c>Play</c> invocations for <see cref="TriggerKind"/>.</summary>
    internal const string ManualTriggerKind = "Manual";

    /// <summary>
    /// Initializes a new instance representing a manual (user-initiated) playback with no trigger
    /// arguments and no cancellation source.
    /// </summary>
    /// <param name="macroName">Logical name of the macro (without the <c>.csx</c> extension).</param>
    public MacroContext(string macroName)
        : this(macroName, Triggers.ManualMacroTrigger.Instance, CancellationToken.None)
    {
    }

    /// <summary>Initializes a new instance with an explicit trigger and cancellation token.</summary>
    /// <param name="macroName">Logical name of the macro (without the <c>.csx</c> extension).</param>
    /// <param name="trigger">The typed trigger that initiated playback; never <see langword="null"/>.</param>
    /// <param name="cancellation">Token observed by the player and forwarded to helper calls.</param>
    /// <exception cref="System.ArgumentNullException">
    /// <paramref name="macroName"/> or <paramref name="trigger"/> is <see langword="null"/>.
    /// </exception>
    public MacroContext(
        string macroName,
        IMacroTrigger trigger,
        CancellationToken cancellation)
    {
        MacroName = macroName ?? throw new System.ArgumentNullException(nameof(macroName));
        Trigger = trigger ?? throw new System.ArgumentNullException(nameof(trigger));
        Cancellation = cancellation;
    }

    /// <inheritdoc />
    public string MacroName { get; }

    /// <inheritdoc />
    public string TriggerKind => Trigger.Kind.ToString();

    /// <inheritdoc />
    public IMacroTrigger Trigger { get; }

    /// <inheritdoc />
    public bool IsManual => Trigger.IsManual;

    /// <inheritdoc />
    public CancellationToken Cancellation { get; }
}
