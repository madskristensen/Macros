// Wired in MacrosPackage.InitializeAsync.

using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Macros.Engine;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace Macros.Recording;

/// <summary>
/// Subscribes to <see cref="IMacroService.RecordingCapReached"/> and surfaces an InfoBar
/// telling the user that recording was automatically stopped and how to adjust the limit.
/// </summary>
/// <remarks>
/// <para>
/// Constructed and stored by <c>MacrosPackage.InitializeAsync</c>. Disposed when the package
/// is disposed, at which point the event subscription is removed.
/// </para>
/// <para>
/// The InfoBar is shown in the shell's global InfoBar host. If that host is unavailable the
/// call is a no-op — no retry, because the user will see the engine state transition to Idle
/// anyway and the InfoBar is informational only.
/// </para>
/// </remarks>
internal sealed class RecordingCapHandler : IDisposable
{
    private readonly IMacroService _service;
    private readonly Microsoft.VisualStudio.Threading.JoinableTaskFactory _jtf;

    private RecordingCapHandler(IMacroService service, AsyncPackage package)
    {
        _service = service;
        _jtf = package.JoinableTaskFactory;
    }

    /// <summary>
    /// Resolves <see cref="IMacroService"/>, subscribes to <see cref="IMacroService.RecordingCapReached"/>,
    /// and returns a <see cref="RecordingCapHandler"/> that owns the subscription lifetime.
    /// </summary>
    public static async Task<RecordingCapHandler> InitializeAsync(AsyncPackage package)
    {
        _ = package ?? throw new ArgumentNullException(nameof(package));

        // Resolve via the package's own service container — NOT VS.GetRequiredServiceAsync.
        // Promotion to the global VS service container only completes AFTER SetSite, so the
        // global lookup throws Assumes+InternalErrorException during package init. The
        // package container is populated synchronously by AddService(...) and is queryable
        // immediately.
        var service = await package.GetServiceAsync(typeof(IMacroService)) as IMacroService
            ?? throw new InvalidOperationException(
                "IMacroService is not registered in the package container.");
        var handler = new RecordingCapHandler(service, package);
        service.RecordingCapReached += handler.OnCapReached;
        return handler;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _service.RecordingCapReached -= OnCapReached;
    }

    private void OnCapReached(object? sender, EventArgs e)
    {
        _jtf.RunAsync(async () =>
        {
            try
            {
                await _jtf.SwitchToMainThreadAsync();

                var model = new InfoBarModel(
                    textSpans: new[]
                    {
                        new InfoBarTextSpan("Recording stopped — reached MaxRecordingSteps. "),
                        new InfoBarHyperlink("Open Settings", "settings"),
                    },
                    image: KnownMonikers.StatusWarning,
                    isCloseButtonVisible: true);

                InfoBar? infoBar = await VS.InfoBar.CreateAsync(model);
                if (infoBar is null)
                {
                    return;
                }

                infoBar.ActionItemClicked += OnInfoBarActionItemClicked;
                await infoBar.TryShowInfoBarUIAsync();
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }).FileAndForget("Macros/RecordingCapHandler/OnCapReached");
    }

    private void OnInfoBarActionItemClicked(object sender, InfoBarActionItemEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (e.ActionItem.ActionContext is string context && context == "settings")
        {
            _jtf.RunAsync(async () =>
            {
                try
                {
                    await VS.Commands.ExecuteAsync("Tools.Options", "Macros");
                }
                catch (Exception ex)
                {
                    await ex.LogAsync();
                }
            }).FileAndForget("Macros/RecordingCapHandler/OpenSettings");
        }

        if (sender is InfoBar bar)
        {
            try
            {
                bar.Close();
            }
            catch
            {
                // Bar already closed — ignore.
            }
        }
    }
}
