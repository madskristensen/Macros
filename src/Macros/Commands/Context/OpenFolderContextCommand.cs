using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace Macros.Commands.Context;

/// <summary>
/// Tool window context-menu handler for "Open Folder in Explorer". Launches Windows
/// Explorer with the macro's .csx file selected so the user can poke at sibling macros or
/// drop the file into source control.
/// </summary>
[Command(PackageGuids.guidMacrosPackageCmdSetString, PackageIds.cmdidMacrosCtxOpenFolder)]
internal sealed class OpenFolderContextCommand : BaseCommand<OpenFolderContextCommand>
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        var current = MacroSelectionContext.Current;
        if (current is null)
        {
            return;
        }

        try
        {
            // /select,"<path>" tells Explorer to open the parent folder and highlight the file.
            // Quoting the path inside the argument string handles spaces in macro folder names.
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{current.Path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            await VS.MessageBox.ShowErrorAsync("Open folder failed", ex.Message);
        }
    }
}
