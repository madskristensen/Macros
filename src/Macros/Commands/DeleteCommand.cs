using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace Macros.Commands;

/// <summary>
/// Stub handler for <c>Macros: Delete</c>. Disabled until M3 introduces the macro list and a
/// selection model the command can act on.
/// </summary>
[Command(PackageGuids.guidMacrosPackageCmdSetString, PackageIds.cmdidMacrosDelete)]
internal sealed class DeleteCommand : BaseCommand<DeleteCommand>
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        await VS.MessageBox.ShowAsync("Delete", "TODO: M3 — delete selected macro.");
    }

    /// <inheritdoc />
    protected override void BeforeQueryStatus(EventArgs e)
    {
        // No selection model yet; keep the entry visible for discoverability but disabled.
        Command.Enabled = false;
    }
}
