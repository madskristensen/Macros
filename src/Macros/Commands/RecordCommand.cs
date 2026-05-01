using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Macros.Engine;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace Macros.Commands;

/// <summary>
/// Handler for the <c>Macros: Record</c> command. Asks <see cref="IMacroService"/> to leave
/// <see cref="MacroState.Idle"/> and start a new recording session.
/// </summary>
/// <remarks>
/// <para>
/// Visibility is primarily driven from the .vsct via the NotRecording UIContext (so the button
/// disappears while a recording is in progress). The <see cref="BeforeQueryStatus"/> override
/// is a belt-and-suspenders disable for keyboard-bound invocations that bypass the visibility
/// constraint.
/// </para>
/// </remarks>
[Command(PackageGuids.CommandSetGuidString, PackageIds.cmdidMacrosRecord)]
internal sealed class RecordCommand : BaseCommand<RecordCommand>
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
            await _service.StartRecordingAsync();
        }
        catch (InvalidOperationException)
        {
            // UIContext should already prevent invocation while not Idle; silently no-op if it
            // races (e.g. a keybinding fires twice before VS re-queries status).
        }
    }

    /// <inheritdoc />
    protected override void BeforeQueryStatus(EventArgs e)
    {
        Command.Enabled = _service is { State: MacroState.Idle };
    }
}
