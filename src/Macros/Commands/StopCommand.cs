using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Macros.Engine;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace Macros.Commands;

/// <summary>
/// Handler for the <c>Macros: Stop</c> command. Ends the active recording session and discards
/// the generated source for M1 — M2 will hand the source off to the storage layer.
/// </summary>
[Command(PackageGuids.CommandSetGuidString, PackageIds.cmdidMacrosStop)]
internal sealed class StopCommand : BaseCommand<StopCommand>
{
    private IMacroService? _service;

    /// <inheritdoc />
    protected override async Task InitializeCompletedAsync()
    {
        _service = await VS.GetRequiredServiceAsync<IMacroService, IMacroService>();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        if (_service is null)
        {
            return;
        }

        try
        {
            // M1: discard the generated source. M2 will route it through MacroStore.
            _ = await _service.StopRecordingAsync();
        }
        catch (InvalidOperationException)
        {
            // Engine wasn't recording; the Recording UIContext should normally prevent this.
        }
    }

    /// <inheritdoc />
    protected override void BeforeQueryStatus(EventArgs e)
    {
        Command.Enabled = _service is { State: MacroState.Recording };
    }
}
