using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Macros.Engine.Storage;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace Macros.Commands.Context;

/// <summary>
/// Tool window context-menu handler for "Rename...". Shows a modal dialog that validates
/// the new name against <see cref="IMacroStore.IsValidName"/> and checks for collisions
/// before committing the rename.
/// </summary>
[Command(PackageGuids.guidMacrosPackageCmdSetString, PackageIds.cmdidMacrosCtxRename)]
internal sealed class RenameContextCommand : BaseCommand<RenameContextCommand>
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        var current = MacroSelectionContext.Current;
        if (current is null) return;

        var storage = await Package.GetServiceAsync(typeof(IMacroStore)) as IMacroStore
            ?? throw new InvalidOperationException("IMacroStore is not registered in the package container.");

        var vm = new RenameDialogViewModel(current, storage);
        var dlg = new RenameDialog { DataContext = vm };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                await storage.RenameAsync(current.Name, vm.NewName, current.Scope);
                await VS.StatusBar.ShowMessageAsync($"Macros: Renamed to \"{vm.NewName}\"");
            }
            catch (Exception ex)
            {
                await VS.MessageBox.ShowErrorAsync("Rename failed", ex.Message);
            }
        }
    }
}
