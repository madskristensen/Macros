using Macros.Engine.Storage;

namespace Macros.Commands.Context;

/// <summary>
/// Pure, testable helper that validates a scope-move operation before any I/O is attempted.
/// Separated from the VS command classes so unit tests can exercise every branch without
/// a VS host.
/// </summary>
internal static class MoveLogic
{
    /// <summary>
    /// Validates whether moving <paramref name="entry"/> to <paramref name="target"/> is
    /// permitted given the current environment.
    /// </summary>
    /// <param name="entry">The macro that the user wants to move.</param>
    /// <param name="target">The destination scope.</param>
    /// <param name="solutionOpen">
    /// Whether a solution is currently open. Must be <see langword="true"/> when
    /// <paramref name="target"/> is <see cref="MacroScope.Repo"/>.
    /// </param>
    /// <returns>
    /// <c>(true, null)</c> when the move is allowed; <c>(false, errorMessage)</c> otherwise.
    /// </returns>
    public static (bool ok, string? error) ValidateMove(
        MacroEntry entry,
        MacroScope target,
        bool solutionOpen)
    {
        if (entry.Scope == target)
        {
            return (false, $"Macro \"{entry.Name}\" is already in the {target} scope.");
        }

        if (target == MacroScope.Repo && !solutionOpen)
        {
            return (false, "Cannot move to Repo scope: no solution is open.");
        }

        return (true, null);
    }
}
