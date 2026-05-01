using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Macros.Engine.Storage;
using Macros.Lifecycle;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace Macros.Commands.Context;

/// <summary>
/// Tool window context-menu handler for "Move to Repo". Visible only when the selected
/// macro lives in the Global scope and a solution is currently open. On execution the
/// macro source is written to the Repo scope and deleted from Global; a conflict
/// (same name already exists in Repo) is resolved by prompting the user to overwrite.
/// </summary>
[Command(PackageGuids.guidMacrosPackageCmdSetString, PackageIds.cmdidMacrosCtxMoveToRepo)]
internal sealed class MoveToRepoCommand : BaseCommand<MoveToRepoCommand>
{
    /// <inheritdoc />
    protected override void BeforeQueryStatus(EventArgs e)
    {
        var current = MacroSelectionContext.Current;
        var solutionOpen = SolutionContextTracker.Current?.HasSolution == true;
        Command.Visible = current?.Scope == MacroScope.Global && solutionOpen;
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
        var (ok, error) = MoveLogic.ValidateMove(current, MacroScope.Repo, solutionOpen);
        if (!ok)
        {
            await VS.MessageBox.ShowErrorAsync("Move to Repo", error ?? string.Empty);
            return;
        }

        var storage = await Package.GetServiceAsync(typeof(IMacroStore)) as IMacroStore
            ?? throw new InvalidOperationException("IMacroStore is not registered in the package container.");
        try
        {
            var source = await storage.LoadByNameAsync(current.Name, MacroScope.Global);
            if (source is null)
            {
                return;
            }

            var existing = await storage.LoadByNameAsync(current.Name, MacroScope.Repo);
            if (existing != null)
            {
                var confirmed = await VS.MessageBox.ShowConfirmAsync(
                    "Conflict",
                    $"A repo macro named \"{current.Name}\" already exists. Overwrite?");
                if (!confirmed)
                {
                    return;
                }
            }

            await storage.SaveAsAsync(current.Name, source, MacroScope.Repo, overwrite: true);
            await storage.DeleteAsync(current.Name, MacroScope.Global);
            await VS.StatusBar.ShowMessageAsync($"Macros: Moved \"{current.Name}\" to repo");
        }
        catch (Exception ex)
        {
            await VS.MessageBox.ShowErrorAsync("Move to Repo", ex.Message);
        }
    }
}
