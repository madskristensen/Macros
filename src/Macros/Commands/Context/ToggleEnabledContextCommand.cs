using Community.VisualStudio.Toolkit;

using Macros.Engine.Triggers;
using Macros.Options;

using Microsoft.VisualStudio.Shell;

using System;
using System.Linq;

using Task = System.Threading.Tasks.Task;

namespace Macros.Commands.Context;

/// <summary>
/// Tool window context-menu handler for the per-macro "Enabled" toggle (issue #8).
/// Flips the disabled flag for the macro currently held in
/// <see cref="MacroSelectionContext.Current"/> via
/// <see cref="MacrosOptions.SetMacroDisabled(string, bool)"/>.
/// </summary>
/// <remarks>
/// The menu item is rendered as a checkable command labelled "Enabled". The check
/// mark is shown when the macro is enabled (the default), and cleared when the user
/// has disabled it. Disabled macros stop firing triggers (the trigger registry
/// filters them out at lookup time) but remain available for manual playback.
/// </remarks>
[Command(PackageGuids.guidMacrosPackageCmdSetString, PackageIds.cmdidMacrosCtxToggleEnabled)]
internal sealed class ToggleEnabledContextCommand : BaseCommand<ToggleEnabledContextCommand>
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        var current = MacroSelectionContext.Current;
        if (current is null)
        {
            return;
        }

        var opts = MacrosOptions.Instance;
        var nowDisabled = !opts.IsMacroDisabled(current.Path);
        opts.SetMacroDisabled(current.Path, nowDisabled);
        await opts.SaveAsync();
    }

    /// <inheritdoc />
    protected override void BeforeQueryStatus(EventArgs e)
    {
        var current = MacroSelectionContext.Current;
        if (current is null)
        {
            Command.Visible = false;
            return;
        }

        // Toggling Enabled only suppresses triggers — for a manual-only macro the flag has
        // no observable effect, so hide the menu item rather than confuse the user with a
        // checkbox that does nothing.
        var hasRealTrigger = current.Triggers is not null
            && current.Triggers.Any(t => t is not null && t.Kind != TriggerKind.Manual);
        if (!hasRealTrigger)
        {
            Command.Visible = false;
            return;
        }

        Command.Visible = true;
        // Checked = enabled (the default state). Unchecked = user-disabled.
        Command.Checked = !MacrosOptions.Instance.IsMacroDisabled(current.Path);
    }
}
