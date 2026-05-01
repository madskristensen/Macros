using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Macros.ToolWindows;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace Macros.Commands;

/// <summary>
/// Handler for the <c>Macros: Refresh</c> command. Reloads the macro list from disk and
/// rebuilds the tool window's view-model. Placed on the tool window toolbar so the user can
/// force a reload without closing and reopening the window.
/// </summary>
[Command(PackageGuids.guidMacrosPackageCmdSetString, PackageIds.cmdidMacrosRefresh)]
internal sealed class RefreshCommand : BaseCommand<RefreshCommand>
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        // Find the existing tool window pane without creating a new one. If the pane hasn't
        // been shown yet there's nothing to refresh; the user will see fresh data on first open.
        var pane = (ToolWindowPane?)Package.FindToolWindow(typeof(MacrosToolWindow.Pane), 0, false);
        if (pane?.Content is MacrosToolWindowControl control &&
            control.DataContext is MacrosToolWindowViewModel vm)
        {
            await vm.LoadAsync();
        }
    }
}
