using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Macros.Engine.Storage;
using Macros.Lifecycle;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace Macros.Commands.Context;

/// <summary>
/// Tool window context-menu handler for "Move to Global". Visible only when the selected
/// macro lives in the Repo scope. On execution the macro source is written to the Global
/// scope and deleted from Repo; a conflict (same name already exists in Global) is
/// resolved by prompting the user to overwrite.
/// </summary>
[Command(PackageGuids.guidMacrosPackageCmdSetString, PackageIds.cmdidMacrosCtxMoveToGlobal)]
internal sealed class MoveToGlobalCommand : BaseCommand<MoveToGlobalCommand>
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

        var solutionOpen = SolutionContextTracker.Current?.HasSolution == true;
        var (ok, error) = MoveLogic.ValidateMove(current, MacroScope.Global, solutionOpen);
        if (!ok)
        {
            await VS.MessageBox.ShowErrorAsync("Move to Global", error ?? string.Empty);
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
            await storage.DeleteAsync(current.Name, MacroScope.Repo);
            await VS.StatusBar.ShowMessageAsync($"Macros: Moved \"{current.Name}\" to global");
        }
        catch (Exception ex)
        {
            await VS.MessageBox.ShowErrorAsync("Move to Global", ex.Message);
        }
    }
}
