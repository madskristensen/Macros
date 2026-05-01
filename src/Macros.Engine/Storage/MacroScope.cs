namespace Macros.Engine.Storage;

/// <summary>
/// Logical home of a named macro file. Determines which on-disk folder
/// <see cref="IMacroStorage"/> reads from and writes to for the named-macro APIs.
/// </summary>
/// <remarks>
/// The scope is a user-facing concept surfaced in the tool window grouping and the
/// <c>Save As</c> dialog. <see cref="Repo"/> requires an open solution; calls to the
/// named-macro APIs with <see cref="Repo"/> when no solution is loaded throw
/// <see cref="System.InvalidOperationException"/>. UI surfaces should hide repo-scoped
/// commands while no solution is open.
/// </remarks>
public enum MacroScope
{
    /// <summary>
    /// The cross-machine library shared by every solution: <c>%APPDATA%\Macros\Macros\</c>
    /// (or the per-user override configured via <c>MacrosOptions.GlobalMacrosFolder</c>).
    /// </summary>
    Global,

    /// <summary>
    /// The per-solution library committed alongside the code: <c>&lt;solution&gt;\.vs\Macros\</c>.
    /// Only available while a solution is open.
    /// </summary>
    Repo,
}
