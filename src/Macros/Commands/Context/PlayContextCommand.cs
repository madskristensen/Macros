using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Macros.Engine;
using Macros.Engine.Player;
using Macros.Errors;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace Macros.Commands.Context;

/// <summary>
/// Tool window context-menu handler for "Play". Replays the macro currently held in
/// <see cref="MacroSelectionContext.Current"/> via <see cref="IMacroService.PlayByNameAsync"/>
/// and routes any failure through <see cref="MacroErrorRenderer"/> so the user sees an
/// infobar, Output pane entry, and Error List entry instead of silent nothing.
/// </summary>
[Command(PackageGuids.guidMacrosPackageCmdSetString, PackageIds.cmdidMacrosCtxPlay)]
internal sealed class PlayContextCommand : BaseCommand<PlayContextCommand>
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        var current = MacroSelectionContext.Current;
        if (current is null)
        {
            return;
        }

        var service = await Package.GetServiceAsync(typeof(IMacroService)) as IMacroService
            ?? throw new InvalidOperationException("IMacroService is not registered in the package container.");

        MacroPlayResult result;
        try
        {
            result = await service.PlayByNameAsync(current.Name, current.Scope);
        }
        catch (InvalidOperationException ex)
        {
            // Engine wasn't Idle (racing trigger could slip through). Synthesize a result
            // so the user still gets surfaced feedback, mirroring PlayLastCommand's pattern.
            result = new MacroPlayResult(
                Success: false,
                CompilationError: ex.Message,
                RuntimeError: null,
                Duration: TimeSpan.Zero);
        }

        if (!result.Success)
        {
            await MacroErrorRenderer.RenderAsync(result, current.Name);
        }
    }
}
