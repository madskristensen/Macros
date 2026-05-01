using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Macros.Engine.Storage;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace Macros.Commands.Context;

/// <summary>
/// Tool window context-menu handler for "Copy to Global". Visible only when the selected
/// macro lives in the Repo scope.
/// </summary>
[Command(PackageGuids.guidMacrosPackageCmdSetString, PackageIds.cmdidMacrosCtxCopyToGlobal)]
internal sealed class CopyToGlobalCommand : BaseCommand<CopyToGlobalCommand>
{
    /// <inheritdoc />
    protected override void BeforeQueryStatus(EventArgs e)
    {
        var current = MacroSelectionContext.Current;
        Command.Visible = current?.Scope == MacroScope.Repo;
        Command.Supported = Command.Visible;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        var current = MacroSelectionContext.Current;
        if (current is null)
        {
            return;
        }

        var storage = await Package.GetServiceAsync(typeof(IMacroStore)) as IMacroStore
            ?? throw new InvalidOperationException("IMacroStore is not registered in the package container.");

        try
        {
            var source = await storage.LoadByNameAsync(current.Name, MacroScope.Repo);
            if (source is null)
            {
                return;
            }

            var existing = await storage.LoadByNameAsync(current.Name, MacroScope.Global);
            if (existing != null)
            {
                var confirmed = await VS.MessageBox.ShowConfirmAsync(
                    "Conflict",
                    $"A global macro named \"{current.Name}\" already exists. Overwrite?");
                if (!confirmed)
                {
                    return;
                }
            }

            await storage.SaveAsAsync(current.Name, source, MacroScope.Global, overwrite: true);
            await VS.StatusBar.ShowMessageAsync($"Macros: Copied \"{current.Name}\" to global");
        }
        catch (Exception ex)
        {
            await VS.MessageBox.ShowErrorAsync("Copy to Global", ex.Message);
        }
    }
}
