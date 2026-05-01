using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace Macros.Commands;

/// <summary>
/// Stub handler for <c>Macros: Edit</c>. Wired in M1 so the cmdid binding is declared; the real
/// implementation lands in M3 when the macro list and the .csx editor open path are in place.
/// </summary>
[Command(PackageGuids.guidMacrosPackageCmdSetString, PackageIds.cmdidMacrosEdit)]
internal sealed class EditCommand : BaseCommand<EditCommand>
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        await VS.MessageBox.ShowAsync("Edit", "TODO: M3 — open macro .csx in the IDE editor.");
    }

    /// <inheritdoc />
    protected override void BeforeQueryStatus(EventArgs e)
    {
        Command.Enabled = false;
    }
}
