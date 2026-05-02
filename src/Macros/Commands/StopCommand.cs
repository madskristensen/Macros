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

        // Subscribe BEFORE calling StopRecordingAsync so we can't race the fire-and-forget
        // save that fires RecordingSaved as soon as it completes the SaveAs.
        var tcs = new TaskCompletionSource<string>();
        EventHandler<RecordingSavedEventArgs> handler = (_, args) => tcs.TrySetResult(args.Path);
        service.RecordingSaved += handler;

        try
        {
            try
            {
                _ = await service.StopRecordingAsync();
            }
            catch (InvalidOperationException)
            {
                // Engine wasn't recording; the Recording UIContext should normally prevent this.
                return;
            }

            // Wait briefly for the background save. If it doesn't land within 5 s (disk full,
            // permission error, etc.) bail silently — the macro is saved-or-lost regardless.
            var winner = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            if (winner == tcs.Task)
            {
                try
                {
                    await VS.Documents.OpenAsync(await tcs.Task);
                }
                catch (Exception ex)
                {
                    // Opening can fail if the path becomes invalid between save and open.
                    // Log but don't surface — the macro is already persisted.
                    await ex.LogAsync();
                }
            }
        }
        finally
        {
            service.RecordingSaved -= handler;
        }
    }

    /// <inheritdoc />
    protected override void BeforeQueryStatus(EventArgs e)
    {
        EnsureResolveStarted();
        Command.Enabled = _service is { State: MacroState.Recording };
    }
}
