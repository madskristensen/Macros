using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Macros.Options;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace Macros.Commands;

/// <summary>
/// Handler for the <c>Macros: Toggle All Triggers</c> command (M4 kill switch).
/// Flips <see cref="MacrosOptions.DisableAllTriggers"/>, saves the setting, and raises
/// <see cref="MacrosOptions.Changed"/> so the status bar updates immediately.
/// </summary>
/// <remarks>
/// The command appears as a checked toolbar button: checked = triggers disabled.
/// Manual macro invocation is unaffected by this flag; only event-triggered and
/// command-triggered macros are suppressed.
/// </remarks>
[Command(PackageGuids.guidMacrosPackageCmdSetString, PackageIds.cmdidMacrosToggleTriggers)]
internal sealed class ToggleTriggersCommand : BaseCommand<ToggleTriggersCommand>
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        var opts = MacrosOptions.Instance;
        opts.DisableAllTriggers = !opts.DisableAllTriggers;
        await opts.SaveAsync();
    }

    /// <inheritdoc />
    protected override void BeforeQueryStatus(EventArgs e)
    {
        // Reflect the current kill-switch state as a checked button.
        Command.Checked = MacrosOptions.Instance.DisableAllTriggers;
    }
}
