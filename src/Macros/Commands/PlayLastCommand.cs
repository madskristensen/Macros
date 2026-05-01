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
[Command(PackageGuids.guidMacrosPackageCmdSetString, PackageIds.cmdidMacrosPlayLast)]
internal sealed class PlayLastCommand : BaseCommand<PlayLastCommand>
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
        }).FileAndForget("Macros/PlayLastCommand/ServiceResolve");
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

        MacroPlayResult result;
        try
        {
            result = await service.PlayCurrentAsync();
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
            string name = service.CurrentMacroName ?? "Macro";
            await MacroErrorRenderer.RenderAsync(result, name);
        }
    }

    /// <inheritdoc />
    protected override void BeforeQueryStatus(EventArgs e)
    {
        EnsureResolveStarted();
        Command.Enabled = _service is { State: MacroState.Idle } && _service.CurrentMacroSource != null;
    }
}
