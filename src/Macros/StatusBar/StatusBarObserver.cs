using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Macros.Engine;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace Macros.StatusBar;

/// <summary>
/// Bridges <see cref="IMacroService.StateChanged"/> to the VS status bar so users always see a
/// passive indicator of whether the macro engine is recording, replaying, or idle.
/// </summary>
/// <remarks>
/// <para>
/// M1 scope: text-only feedback ("Macros: Recording...", "Macros: Replaying...") with the
/// general spinning animation icon while recording. M2/M3 may layer richer status (current
/// macro name, progress) on top of the same hook.
/// </para>
/// <para>
/// Initialization is fire-and-forget from <c>MacrosPackage.InitializeAsync</c>: the observer
/// resolves <see cref="IMacroService"/> via <see cref="VS"/>, subscribes to its event, and
/// then handles every transition by hopping to the UI thread to call into
/// <see cref="IVsStatusbar"/>. All exceptions are logged via
/// <c>Community.VisualStudio.Toolkit</c> so a failed status-bar update can never bring the
/// package down.
/// </para>
/// </remarks>
internal static class StatusBarObserver
{
    private const string RecordingText = "Macros: Recording...";
    private const string ReplayingText = "Macros: Replaying...";

    private static AsyncPackage? _package;
    private static IMacroService? _service;
    private static int _recordingCount;

    /// <summary>
    /// Resolves <see cref="IMacroService"/>, subscribes to its <see cref="IMacroService.StateChanged"/>
    /// event, and pushes an initial status reflecting the current state.
    /// </summary>
    /// <param name="package">The owning <see cref="AsyncPackage"/>; used to resolve <see cref="IVsStatusbar"/>.</param>
    public static async Task InitializeAsync(AsyncPackage package)
    {
        if (package == null)
        {
            throw new ArgumentNullException(nameof(package));
        }

        _package = package;

        try
        {
            _service = await VS.GetRequiredServiceAsync<IMacroService, IMacroService>();
            _service.StateChanged += OnStateChanged;
            _service.RecordingStepCountChanged += OnStepCountChanged;

            // Push initial state so the bar is correct even if the engine starts non-Idle later
            // (defensive — M1 always boots in Idle).
            await ApplyStateAsync(_service.State);
        }
        catch (Exception ex)
        {
            await ex.LogAsync();
        }
    }

    private static void OnStateChanged(object sender, MacroStateChangedEventArgs e)
    {
        if (e.NewState == MacroState.Recording)
        {
            _recordingCount = 0;
        }

        // StateChanged fires on whatever thread did the transition; hop to the UI thread before
        // touching IVsStatusbar. RunAsync forks a JoinableTask so we don't block the engine.
        AsyncPackage? package = _package;
        if (package == null)
        {
            return;
        }

        package.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await ApplyStateAsync(e.NewState);
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }).FileAndForget("macros/statusbar/statechanged");
    }

    private static void OnStepCountChanged(object sender, int count)
    {
        _recordingCount = count;

        AsyncPackage? package = _package;
        IMacroService? service = _service;
        if (package == null || service == null)
        {
            return;
        }

        int maxSteps = service.CurrentRecordingMaxSteps;

        package.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await UpdateRecordingCountTextAsync(count, maxSteps);
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
            }
        }).FileAndForget("macros/statusbar/stepcountchanged");
    }

    private static async Task UpdateRecordingCountTextAsync(int count, int maxSteps)
    {
        AsyncPackage? package = _package;
        if (package == null)
        {
            return;
        }

        await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

        if (await package.GetServiceAsync(typeof(SVsStatusbar)) is not IVsStatusbar statusbar)
        {
            return;
        }

        // Only update the text if we're still recording.
        IMacroService? service = _service;
        if (service?.State != MacroState.Recording)
        {
            return;
        }

        // Soft warning: once count reaches 50 % of cap, include the counter in the text.
        // Yellow color via IVsStatusbar2 is not available in all VS shell versions; we skip
        // color and document the gap here. TODO: investigate IVsStatusbar2.SetBackgroundColor.
        string text = (maxSteps != int.MaxValue && count >= maxSteps / 2)
            ? $"Macros: Recording... ({count}/{maxSteps})"
            : RecordingText;

        statusbar.SetText(text);
    }

    private static async Task ApplyStateAsync(MacroState state)
    {
        AsyncPackage? package = _package;
        if (package == null)
        {
            return;
        }

        await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

        if (await package.GetServiceAsync(typeof(SVsStatusbar)) is not IVsStatusbar statusbar)
        {
            return;
        }

        // SetText is harmless even if the bar is "frozen" — it just queues; freeze state is owned
        // by other shell components and we deliberately don't fight them for it here.
        switch (state)
        {
            case MacroState.Recording:
                statusbar.SetText(RecordingText);
                SetAnimation(statusbar, on: true);
                break;

            case MacroState.Playing:
                SetAnimation(statusbar, on: false);
                statusbar.SetText(ReplayingText);
                break;

            case MacroState.Idle:
            default:
                SetAnimation(statusbar, on: false);
                statusbar.SetText(string.Empty);
                break;
        }
    }

    private static void SetAnimation(IVsStatusbar statusbar, bool on)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // IVsStatusbar.Animation takes the icon as ref object. Pass the SBAI_General short
        // boxed into object so the marshaller hands VS the variant it expects.
        object icon = (short)Constants.SBAI_General;
        statusbar.Animation(on ? 1 : 0, ref icon);
    }
}
