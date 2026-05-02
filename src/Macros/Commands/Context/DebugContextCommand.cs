using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Macros.Engine;
using Macros.Engine.Player;
using Macros.Errors;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace Macros.Commands.Context;

/// <summary>
/// Tool window context-menu handler for "Debug Macro". Attaches a debugger to the current
/// devenv.exe process (via <see cref="System.Diagnostics.Debugger.Launch"/>) then plays the
/// macro so the attached debugger can pause at breakpoints in the <c>.csx</c> source.
/// </summary>
/// <remarks>
/// <para>
/// Because a process cannot debug itself, the user must attach a <em>second</em> VS instance
/// (or any managed debugger). <see cref="System.Diagnostics.Debugger.Launch"/> opens the JIT
/// debugger selection dialog; the user picks the debugger, then execution continues with the
/// macro running under full source-level debugging.
/// </para>
/// <para>
/// This command relies on Phase 1 infrastructure: <c>WithEmitDebugInformation(true)</c> and
/// <c>WithFilePath(csxPath)</c> in <see cref="MacroPlayer"/>, which ensure the in-memory PDB
/// maps sequence points to the original <c>.csx</c> file.
/// </para>
/// </remarks>
[Command(PackageGuids.guidMacrosPackageCmdSetString, PackageIds.cmdidMacrosCtxDebug)]
internal sealed class DebugContextCommand : BaseCommand<DebugContextCommand>
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        var current = MacroSelectionContext.Current;
        if (current is null)
            return;

        // Prompt the user before showing the JIT debugger dialog.
        if (!System.Diagnostics.Debugger.IsAttached)
        {
            bool shouldProceed = await VS.MessageBox.ShowConfirmAsync(
                "Debug Macro",
                "A debugger selection dialog will appear. Choose a Visual Studio instance " +
                "(or 'New instance of Visual Studio') to attach as the debugger.\n\n" +
                "After attaching, you can set breakpoints in the .csx file and step through the macro.");

            if (!shouldProceed)
                return;

            // Launch the JIT debugger dialog on a background thread to avoid blocking the pump.
            bool attached = await Task.Run(() => System.Diagnostics.Debugger.Launch());
            if (!attached)
                return;
        }

        // Now play the macro normally — the attached debugger will break at any
        // Debugger.Break() calls or user-set breakpoints in the .csx source.
        var service = await Package.GetServiceAsync(typeof(IMacroService)) as IMacroService
            ?? throw new InvalidOperationException("IMacroService is not registered.");

        MacroPlayResult result;
        try
        {
            result = await service.PlayByNameAsync(current.Name, current.Scope);
        }
        catch (InvalidOperationException ex)
        {
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
