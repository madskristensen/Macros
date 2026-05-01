using Macros.Engine.Storage;
using Macros.Options;

namespace Macros.Trust;

/// <summary>
/// Pure decision helper that gates trigger-driven dispatch of a <see cref="MacroEntry"/>
/// against the per-solution trust state stored in <see cref="MacrosOptions"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope policy.</b>
/// </para>
/// <list type="bullet">
///   <item><see cref="MacroScope.Global"/> macros live in the user's APPDATA library, so
///         they're implicitly trusted: whatever ships there was put there by the user
///         (recording, drag-drop, manual save) and re-using the same files across every
///         solution makes per-solution gating meaningless.</item>
///   <item><see cref="MacroScope.Repo"/> macros live in <c>&lt;solution&gt;\.vs\Macros</c>
///         and may have been authored by anyone who pushed to the branch. They only run
///         from a trigger when the user has explicitly trusted the current solution
///         <em>and</em> not blocked it (block always wins; see
///         <see cref="MacrosOptions.IsSolutionBlocked"/>).</item>
/// </list>
/// <para>
/// <b>Manual playback is not gated.</b> Hitting Run in the tool window or invoking the
/// Run-Last command is an explicit user action, so the trust prompt — if any — surfaces
/// at that level rather than here. <see cref="IsAllowed"/> is intended exclusively for
/// the trigger dispatchers (<c>CommandTriggerDispatcher</c>, <c>EventTriggerDispatcher</c>).
/// </para>
/// <para>
/// <b>Fail-safe default.</b> When no solution is open the helper denies repo-scoped
/// dispatch even if the macro somehow surfaced from a prior enumeration: a repo macro
/// without a solution context cannot be attributed to a trusted source.
/// </para>
/// </remarks>
internal static class TrustGate
{
    /// <summary>
    /// Evaluates whether a triggered macro execution is allowed for the given entry.
    /// </summary>
    /// <param name="entry">The macro library entry the dispatcher is about to run.</param>
    /// <param name="options">The persisted options snapshot. Tests inject an isolated
    /// instance; the production dispatchers pass <see cref="MacrosOptions.Instance"/>.</param>
    /// <param name="currentSolutionPath">The full <c>.sln</c> path of the currently open
    /// solution (typically <c>SolutionContextTracker.Current.GetCurrentSolutionPath()</c>),
    /// or <see langword="null"/> when no solution is open. Repo macros without a solution
    /// context are denied; global macros are unaffected by this argument.</param>
    /// <returns>
    /// <see langword="true"/> when the macro may be dispatched by a trigger;
    /// <see langword="false"/> when it must be silently skipped.
    /// </returns>
    public static bool IsAllowed(MacroEntry entry, MacrosOptions options, string? currentSolutionPath)
    {
        if (entry is null) return false;
        if (options is null) return false;

        if (entry.Scope == MacroScope.Global)
        {
            return true;
        }

        if (entry.Scope == MacroScope.Repo)
        {
            if (string.IsNullOrEmpty(currentSolutionPath)) return false;
            // Block always wins over trust — a solution that's been explicitly blocked
            // cannot have its repo triggers fire even if a stale Trust entry survives
            // from a previous session.
            if (options.IsSolutionBlocked(currentSolutionPath)) return false;
            return options.IsSolutionTrusted(currentSolutionPath);
        }

        return false;
    }
}
