using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Macros.Engine;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace Macros.Commands.Context;

/// <summary>
/// Tool window context-menu handler for "Play". Replays the macro currently held in
/// <see cref="MacroSelectionContext.Current"/> via <see cref="IMacroService.PlayByNameAsync"/>.
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
        await service.PlayByNameAsync(current.Name, current.Scope);
    }
}
