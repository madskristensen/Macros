using Macros.Engine.Storage;

namespace Macros.Commands.Context;

/// <summary>
/// Stores the macro-group header that was right-clicked just before the group-level context
/// menu is shown, so commands can resolve whether they should target Global or Repo scope.
/// </summary>
internal static class MacroGroupSelectionContext
{
    public static MacroScope? CurrentScope { get; private set; }

    public static void SetFromHeader(string? header)
    {
        CurrentScope = header switch
        {
            "Global" => MacroScope.Global,
            "Repo" => MacroScope.Repo,
            _ => null,
        };
    }
}
