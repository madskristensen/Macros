using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace Macros.Commands;

/// <summary>
/// Stub handler for <c>Macros: Rename</c>. Disabled until M3 introduces the macro list and a
/// selection model the command can act on.
/// </summary>
[Command(PackageGuids.CommandSetGuidString, PackageIds.cmdidMacrosRename)]
internal sealed class RenameCommand : BaseCommand<RenameCommand>
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        await VS.MessageBox.ShowAsync("Rename", "TODO: M3 — rename selected macro.");
    }

    /// <inheritdoc />
    protected override void BeforeQueryStatus(EventArgs e)
    {
        Command.Enabled = false;
    }
}
