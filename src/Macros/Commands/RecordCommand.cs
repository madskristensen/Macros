using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Macros.Engine;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace Macros.Commands;

/// <summary>
/// Handler for the <c>Macros: Record</c> command. Starts a new recording session when idle,
/// and stops the active recording when invoked by its keyboard shortcut while recording.
/// </summary>
/// <remarks>
/// <para>
/// Visibility is primarily driven from the .vsct via the NotRecording UIContext (so the button
/// disappears while a recording is in progress). The command remains enabled while recording so
/// the Ctrl+Shift+R key binding can stop the session.
/// </para>
/// </remarks>
[Command(PackageGuids.guidMacrosPackageCmdSetString, PackageIds.cmdidMacrosRecord)]
internal sealed class RecordCommand : BaseCommand<RecordCommand>
{
    private IMacroService? _service;
    private bool _resolveStarted;

    /// <summary>
    /// Lazily resolves <see cref="IMacroService"/> on first use and caches the result.
    /// Uses the package container directly instead of <c>ServiceProvider.GlobalProvider</c>,
    /// which does not reliably surface promoted custom services after <c>SetSite</c> returns.
    /// </summary>
    private async Task<IMacroService> GetServiceAsync()
    {
        if (_service is not null) return _service;
        return _service = await Package.GetServiceAsync(typeof(IMacroService)) as IMacroService
            ?? throw new InvalidOperationException(
                "IMacroService is not registered in the package container.");
    }

    /// <summary>
    /// Kicks off background resolution the first time VS queries the command's status, so the
    /// menu/toolbar button can transition from disabled to enabled without the user having to
    /// click first. Fire-and-forget on the package's <see cref="JoinableTaskFactory"/>; any
    /// failure is logged and the flag reset so a future query retries.
    /// </summary>
    private void EnsureResolveStarted()
    {
        if (_service is not null || _resolveStarted) return;
        _resolveStarted = true;
        Package.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await GetServiceAsync();
            }
            catch (Exception ex)
            {
                _resolveStarted = false;
                await ex.LogAsync();
            }
        }).FileAndForget("Macros/RecordCommand/ServiceResolve");
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        IMacroService service;
        try
        {
            service = await GetServiceAsync();
        }
        catch (Exception ex)
        {
            await ex.LogAsync();
            return;
        }

        try
        {
            if (service.State == MacroState.Idle)
            {
                await service.StartRecordingAsync();
            }
            // Recording state: BeforeQueryStatus disables the command so it shouldn't fire
            // here. The defensive no-op stays in case a UIContext race lets the click through.
        }
        catch (InvalidOperationException)
        {
            // UIContext should already prevent invalid transitions; silently no-op if it races.
        }
    }

    /// <inheritdoc />
    protected override void BeforeQueryStatus(EventArgs e)
    {
        EnsureResolveStarted();
        // Issue #6: disable Record while a recording is in progress. The visible Stop
        // button (swapped in via VSCT VisibilityItem on guidMacrosRecordingContext) is the
        // only path for ending the recording from the toolbar; the Ctrl+Shift+R hotkey
        // stops being a toggle as a result.
        Command.Enabled = _service is { State: MacroState.Idle };
    }
}
