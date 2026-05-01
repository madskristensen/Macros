using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine.Storage;

namespace Macros.Engine.Triggers;

/// <summary>
/// One match returned by <see cref="IMacroTriggerRegistry"/>: the macro entry whose header
/// declared the trigger directive paired with the parsed <see cref="TriggerBinding"/>.
/// </summary>
/// <remarks>
/// A single macro can declare multiple <c>@trigger</c> directives — each registered name
/// surfaces as its own <see cref="TriggerMatch"/>. Filter evaluation (the <c>when key=val</c>
/// clause on VsEvent triggers) happens at dispatch time, not at registration time, so the
/// registry returns every binding whose name matches and lets the dispatcher narrow further.
/// </remarks>
/// <param name="Entry">The library entry that contributed this trigger.</param>
/// <param name="Binding">The parsed directive (kind, name, optional filters).</param>
public sealed record TriggerMatch(MacroEntry Entry, TriggerBinding Binding);

/// <summary>
/// Index over the macro library that answers "which macros want to run for trigger X?".
/// Populated by scanning every <see cref="MacroEntry.Triggers"/> from <see cref="IMacroStore.ListAllAsync"/>
/// and rebuilt whenever the library changes. Consumed by the trigger dispatchers
/// (event bus + command observer hot path).
/// </summary>
/// <remarks>
/// <para>
/// The registry is global (one per package). Per-solution trust gating happens upstream in
/// the composite store — when a solution is untrusted the repo store contributes no entries,
/// so the registry naturally indexes only what's allowed to run.
/// </para>
/// <para>
/// Lookups are O(1) for the hot-path command checks (<see cref="HasBeforeCommand"/> /
/// <see cref="HasAfterCommand"/>) and O(1) average for the full <see cref="FindByEvent"/>
/// / <see cref="FindByBeforeCommand"/> / <see cref="FindByAfterCommand"/> calls. The dispatcher
/// is expected to gate the expensive lookups behind the boolean fast-path so that the IDE
/// command pipeline stays effectively zero-cost when no command triggers exist.
/// </para>
/// <para>
/// The kill-switch (<c>MacrosOptions.DisableAllTriggers</c>) is re-checked on every query,
/// so flipping the flag in Tools → Options takes effect immediately without forcing a reload.
/// All query methods short-circuit to an empty result while the kill-switch is on; the
/// indexes themselves stay populated so the registry resumes work the moment the flag flips
/// back to off.
/// </para>
/// </remarks>
public interface IMacroTriggerRegistry : IDisposable
{
    /// <summary>
    /// Returns every macro whose header declares a <see cref="TriggerKind.VsEvent"/> trigger
    /// for <paramref name="canonicalEventName"/> (e.g. <c>"Build.SolutionBuildDone"</c>).
    /// Names are matched case-insensitively. Returns an empty list while the kill-switch is on
    /// or before the registry has finished its initial load.
    /// </summary>
    IReadOnlyList<TriggerMatch> FindByEvent(string canonicalEventName);

    /// <summary>
    /// Returns every macro whose header declares <c>@trigger BeforeCommand &lt;commandName&gt;</c>.
    /// Names are matched case-insensitively. Returns an empty list while the kill-switch is on
    /// or before the registry has finished its initial load.
    /// </summary>
    IReadOnlyList<TriggerMatch> FindByBeforeCommand(string commandName);

    /// <summary>
    /// Returns every macro whose header declares <c>@trigger AfterCommand &lt;commandName&gt;</c>.
    /// Names are matched case-insensitively. Returns an empty list while the kill-switch is on
    /// or before the registry has finished its initial load.
    /// </summary>
    IReadOnlyList<TriggerMatch> FindByAfterCommand(string commandName);

    /// <summary>
    /// Hot-path probe: returns <see langword="true"/> when at least one macro has a
    /// <see cref="TriggerKind.BeforeCommand"/> binding for <paramref name="commandName"/>.
    /// O(1) hash-set lookup, intended to sit at the top of <c>CommandObserver.QueryStatus</c>
    /// and <c>Exec</c> so the per-command hot path costs nothing when no triggers are wired.
    /// Returns <see langword="false"/> while the kill-switch is on.
    /// </summary>
    bool HasBeforeCommand(string commandName);

    /// <summary>
    /// Hot-path probe for <see cref="TriggerKind.AfterCommand"/>. See <see cref="HasBeforeCommand"/>
    /// for semantics.
    /// </summary>
    bool HasAfterCommand(string commandName);

    /// <summary>
    /// Gets a value indicating whether the registry has completed at least one
    /// <see cref="RefreshAsync"/> cycle since construction. The registry pre-populates on
    /// init (fire-and-forget) so consumers can poll this for diagnostics; queries themselves
    /// safely return empty before the first load.
    /// </summary>
    bool IsLoaded { get; }

    /// <summary>
    /// Forces a full reload from the underlying <see cref="IMacroStore"/>, rebuilding every
    /// index from scratch. Safe to call from any thread; concurrent calls coalesce so the
    /// store is queried once per logical change burst rather than once per caller.
    /// </summary>
    Task RefreshAsync(CancellationToken cancellation = default);

    /// <summary>
    /// Raised after a successful <see cref="RefreshAsync"/> cycle has swapped in new indexes.
    /// Fires on the threadpool — UI subscribers must marshal to the main thread before
    /// touching WPF state.
    /// </summary>
    event EventHandler? Changed;
}
