using System.Collections.Generic;
using System.Linq;
using Macros.Engine.Storage;
using Macros.Engine.Triggers;

namespace Macros.Trust;

/// <summary>
/// Pure decision helper for the M4 trust-gate InfoBar. Extracted from
/// <see cref="TrustGateInfoBar"/> so the show / hide logic can be unit-tested without
/// standing up a VS host: the InfoBar wrapper, <see cref="Macros.Options.MacrosOptions"/>,
/// and the solution tracker all require the shell, but the decision itself is a function
/// over four plain inputs.
/// </summary>
internal static class TrustGateLogic
{
    /// <summary>
    /// Returns whether the trust-gate InfoBar should be shown for the current solution
    /// and the count of repo macros that carry at least one auto-run trigger.
    /// </summary>
    /// <param name="solutionPath">
    /// Active solution path (typically the directory reported by
    /// <see cref="Macros.Lifecycle.SolutionContextTracker.GetCurrentSolutionDirectory"/>).
    /// <see langword="null"/> when no solution is open.
    /// </param>
    /// <param name="isTrusted">Result of <see cref="Macros.Options.MacrosOptions.IsSolutionTrusted"/>.</param>
    /// <param name="isBlocked">Result of <see cref="Macros.Options.MacrosOptions.IsSolutionBlocked"/>.</param>
    /// <param name="repoMacros">
    /// All macros currently enumerated in <see cref="MacroScope.Repo"/>. Pass an empty
    /// list when no solution is open or the repo folder is empty.
    /// </param>
    /// <returns>
    /// <c>show</c> — <see langword="true"/> when the InfoBar should be displayed.
    /// <c>triggeredCount</c> — number of repo macros with at least one non-Manual trigger.
    /// Always zero when <c>show</c> is <see langword="false"/>.
    /// </returns>
    public static (bool show, int triggeredCount) ShouldShow(
        string? solutionPath,
        bool isTrusted,
        bool isBlocked,
        IReadOnlyList<MacroEntry> repoMacros)
    {
        if (solutionPath is null)
        {
            return (false, 0);
        }

        if (isTrusted || isBlocked)
        {
            return (false, 0);
        }

        if (repoMacros is null || repoMacros.Count == 0)
        {
            return (false, 0);
        }

        var triggered = repoMacros.Count(m =>
            m.Triggers != null && m.Triggers.Any(t => t.Kind != TriggerKind.Manual));

        return (triggered > 0, triggered);
    }
}
