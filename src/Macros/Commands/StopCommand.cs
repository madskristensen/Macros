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
[Command(PackageGuids.guidMacrosPackageCmdSetString, PackageIds.cmdidMacrosStop)]
internal sealed class StopCommand : BaseCommand<StopCommand>
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
        }).FileAndForget("Macros/StopCommand/ServiceResolve");
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
            await RecordingCommandUtilities.StopRecordingAndOpenAsync(service);
        }
        catch (InvalidOperationException)
        {
            // Engine wasn't recording; the Recording UIContext should normally prevent this.
        }
    }

    /// <inheritdoc />
    protected override void BeforeQueryStatus(EventArgs e)
    {
        EnsureResolveStarted();
        Command.Enabled = _service is { State: MacroState.Recording };
    }
}
