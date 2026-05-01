using Macros.Engine.Storage;

namespace Macros.Commands.Context;

/// <summary>
/// Single-selection bridge between the tool window's right-click handler and the VSCT-based
/// context menu commands. The tool window writes <see cref="Current"/> immediately before
/// calling <c>IVsUIShell.ShowContextMenu</c>; each <c>Macros.Context.*</c> command reads
/// it during <c>ExecuteAsync</c>.
/// </summary>
/// <remarks>
/// Both the writer (right-click in the WPF UI) and the readers (Toolkit
/// <c>BaseCommand.ExecuteAsync</c> dispatched by VS) run on the UI thread, so a plain
/// static field is sufficient — no locking required. Multi-selection (Ctrl-click etc.)
/// is intentionally out of scope for M3.
/// </remarks>
internal static class MacroSelectionContext
{
    /// <summary>
    /// The macro the user right-clicked, or <see langword="null"/> when no row has been
    /// targeted yet. Context-menu command handlers must treat <see langword="null"/> as
    /// a no-op to defend against the menu being invoked through some unexpected path.
    /// </summary>
    public static MacroEntry? Current { get; set; }
}
