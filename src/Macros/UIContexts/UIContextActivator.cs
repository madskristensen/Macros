using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Macros.Engine;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

using Task = System.Threading.Tasks.Task;

namespace Macros.UIContexts;

/// <summary>
/// Keeps the <c>RecordingContext</c> and <c>NotRecordingContext</c> UIContexts in sync with
/// <see cref="IMacroService.State"/> so VSCT <c>VisibilityItem</c> rules flip the Record / Stop
/// toolbar buttons correctly at runtime.
/// </summary>
/// <remarks>
/// <para>
/// The two UIContext GUIDs are declared in <see cref="PackageGuids"/> and referenced in
/// <c>VSCommandTable.vsct</c>. This class is the only place that ever calls
/// <c>IVsMonitorSelection.SetCmdUIContext</c> for those two cookies.
/// </para>
/// <para>
/// Production entry point: <see cref="InitializeAsync"/>. Test entry point:
/// <see cref="CreateForTests"/>. Both ultimately delegate UI-context writes through the
/// <c>setCmdUIContext</c> action injected at construction time, keeping VS shell
/// dependencies out of unit tests.
/// </para>
/// </remarks>
internal sealed class UIContextActivator : IDisposable
{
    private readonly IMacroService _service;
    private readonly JoinableTaskFactory _jtf;
    private readonly Action<uint, int> _setCmdUIContext;
    private readonly uint _recordingCookie;
    private readonly uint _notRecordingCookie;

    private UIContextActivator(
        IMacroService service,
        JoinableTaskFactory jtf,
        Action<uint, int> setCmdUIContext,
        uint recCookie,
        uint notRecCookie)
    {
        _service = service;
        _jtf = jtf;
        _setCmdUIContext = setCmdUIContext;
        _recordingCookie = recCookie;
        _notRecordingCookie = notRecCookie;
    }

    /// <summary>
    /// Resolves all VS services, subscribes to <see cref="IMacroService.StateChanged"/>, and
    /// applies the current state immediately so the toolbar reflects reality on first show.
    /// </summary>
    public static async Task<UIContextActivator> InitializeAsync(AsyncPackage package)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();

        var monitor = await package.GetServiceAsync(typeof(SVsShellMonitorSelection)) as IVsMonitorSelection
            ?? throw new InvalidOperationException("SVsShellMonitorSelection service unavailable.");

        var recGuid = PackageGuids.guidMacrosRecordingContext;
        monitor.GetCmdUIContextCookie(ref recGuid, out uint recCookie);

        var notRecGuid = PackageGuids.guidMacrosNotRecordingContext;
        monitor.GetCmdUIContextCookie(ref notRecGuid, out uint notRecCookie);

        var service = await package.GetServiceAsync(typeof(IMacroService)) as IMacroService
            ?? throw new InvalidOperationException(
                "IMacroService is not registered in the package container.");

        // Capture the monitor in a closure; SetCmdUIContext callers are responsible for
        // being on the UI thread (ApplyStateAsync always switches first).
        void SetContext(uint cookie, int active)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            monitor.SetCmdUIContext(cookie, active);
        }

        var activator = new UIContextActivator(service, package.JoinableTaskFactory, SetContext, recCookie, notRecCookie);
        service.StateChanged += activator.OnStateChanged;
        await activator.ApplyStateAsync(service.State);
        return activator;
    }

    /// <summary>
    /// Creates an instance suitable for unit tests: no VS shell required; the
    /// <paramref name="setCmdUIContext"/> action replaces the real
    /// <c>IVsMonitorSelection.SetCmdUIContext</c> call. Subscribes to
    /// <see cref="IMacroService.StateChanged"/> so <see cref="Dispose"/> can unsubscribe.
    /// </summary>
    internal static UIContextActivator CreateForTests(
        IMacroService service,
        JoinableTaskFactory jtf,
        Action<uint, int> setCmdUIContext,
        uint recCookie,
        uint notRecCookie)
    {
        var activator = new UIContextActivator(service, jtf, setCmdUIContext, recCookie, notRecCookie);
        service.StateChanged += activator.OnStateChanged;
        return activator;
    }

    private void OnStateChanged(object? sender, MacroStateChangedEventArgs e)
    {
        // StateChanged may fire from any thread; fire-and-forget a hop to the UI thread.
        _jtf.RunAsync(async () =>
        {
            await ApplyStateAsync(e.NewState);
        }).FileAndForget("Macros/UIContextActivator");
    }

    /// <summary>
    /// Switches to the UI thread then writes both UIContext cookies atomically.
    /// </summary>
    internal async Task ApplyStateAsync(MacroState state)
    {
        await _jtf.SwitchToMainThreadAsync();

        int rec = state == MacroState.Recording ? 1 : 0;
        _setCmdUIContext(_recordingCookie, rec);
        _setCmdUIContext(_notRecordingCookie, rec == 1 ? 0 : 1);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _service.StateChanged -= OnStateChanged;
    }
}
