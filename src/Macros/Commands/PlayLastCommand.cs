using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Macros.Engine;
using Macros.Engine.Player;
using Macros.Errors;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace Macros.Commands;

/// <summary>
/// Handler for the <c>Macros: Play Last</c> command. Replays
/// <see cref="IMacroService.CurrentMacroSource"/> via <see cref="IMacroService.PlayCurrentAsync"/>
/// and routes any failure (compile error, runtime exception, missing source) through
/// <see cref="MacroErrorRenderer"/> for surfacing in the Output pane, Error List, and InfoBar.
/// </summary>
[Command(PackageGuids.CommandSetGuidString, PackageIds.cmdidMacrosPlayLast)]
internal sealed class PlayLastCommand : BaseCommand<PlayLastCommand>
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

        MacroPlayResult result;
        try
        {
            result = await _service.PlayCurrentAsync();
        }
        catch (InvalidOperationException ex)
        {
            // Engine wasn't Idle (BeforeQueryStatus normally prevents this, but a racing trigger
            // could slip through). Synthesize a result so the user still gets surfaced feedback.
            result = new MacroPlayResult(
                Success: false,
                CompilationError: ex.Message,
                RuntimeError: null,
                Duration: TimeSpan.Zero);
        }

        if (!result.Success)
        {
            string name = _service.CurrentMacroName ?? "Macro";
            await MacroErrorRenderer.RenderAsync(result, name);
        }
    }

    /// <inheritdoc />
    protected override void BeforeQueryStatus(EventArgs e)
    {
        Command.Enabled = _service is { State: MacroState.Idle } && _service.CurrentMacroSource != null;
    }
}
