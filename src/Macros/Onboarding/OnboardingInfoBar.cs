// Wired in MacrosPackage.InitializeAsync.

using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Macros.Options;
using Macros.Samples;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

namespace Macros.Onboarding;

/// <summary>
/// One-shot, first-run onboarding InfoBar. Greets the user with the record/play hotkeys and a
/// shortcut to open the Macros tool window. Self-disables after it has been shown the first time.
/// </summary>
/// <remarks>
/// <para>
/// Hosted in the active document window when one is available, otherwise the InfoBar is anchored
/// to the shell's main InfoBar host. If neither host is available (e.g. the package loaded before
/// the shell finished raising the relevant frames), the call is a no-op and another attempt is
/// made on the next package load — the <see cref="MacrosOptions.FirstRun"/> flag is only flipped
/// once the InfoBar has actually been displayed.
/// </para>
/// <para>
/// The class is safely re-callable: <see cref="ShowIfFirstRunAsync"/> guards against re-entry
/// using <see cref="Interlocked"/> and bails out immediately when the persisted flag indicates the
/// user has already seen the bar.
/// </para>
/// <para>
/// This is intentionally separate from the M4 Trust Gate InfoBar (<c>m4-trust-gate-infobar</c>),
/// which is a different bar with different action items, shown under different conditions.
/// </para>
/// </remarks>
internal static class OnboardingInfoBar
{
    private const string OpenWindowActionContext = "macros.onboarding.openWindow";
    private const string DismissActionContext = "macros.onboarding.dismiss";

    private static int _attemptInFlight;

    /// <summary>
    /// Builds the onboarding message text. Derives the sample count from
    /// <see cref="SampleTemplateProvider"/> so the message stays accurate as the gallery grows
    /// or shrinks; falls back to a count-free phrasing if the provider can't be queried.
    /// </summary>
    /// <param name="sampleCount">
    /// Number of built-in samples. Pass <c>0</c> or a negative value to omit the gallery hint.
    /// </param>
    /// <returns>The complete InfoBar message string.</returns>
    internal static string BuildOnboardingMessage(int sampleCount)
    {
        if (sampleCount > 0)
        {
            return $"Macros installed — {sampleCount} ready-to-use samples are in the tool window. " +
                "Press Ctrl+Shift+R to record, Ctrl+Shift+P to play.";
        }

        return "Macros installed. Press Ctrl+Shift+R to record, Ctrl+Shift+P to play.";
    }

    /// <summary>
    /// Shows the first-run onboarding InfoBar if it has not yet been seen. Safe to call multiple
    /// times: subsequent calls are no-ops once the bar has been shown (or while a previous
    /// attempt is still in flight).
    /// </summary>
    public static async Task ShowIfFirstRunAsync()
    {
        if (Interlocked.Exchange(ref _attemptInFlight, 1) == 1)
        {
            return;
        }

        bool persistedShown = false;

        try
        {
            MacrosOptions options = await MacrosOptions.GetLiveInstanceAsync();
            if (!options.FirstRun)
            {
                return;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            InfoBarModel model = BuildModel();
            InfoBar? infoBar = await TryCreateInfoBarAsync(model);

            if (infoBar == null)
            {
                // No host available yet — defer. Allow another attempt next package load.
                return;
            }

            infoBar.ActionItemClicked += OnActionItemClicked;

            bool shown = await infoBar.TryShowInfoBarUIAsync();
            if (!shown)
            {
                infoBar.ActionItemClicked -= OnActionItemClicked;
                return;
            }

            // Persist immediately. Once the bar has been displayed we never show it again,
            // regardless of how the user dismisses it (action item, close button, or VS shutdown).
            options.FirstRun = false;
            await options.SaveAsync();
            persistedShown = true;
        }
        catch (Exception ex)
        {
            await ex.LogAsync();
        }
        finally
        {
            // Only release the in-flight guard if we did not actually persist a successful show.
            // Once persisted, FirstRun==false short-circuits any subsequent call cheaply.
            if (!persistedShown)
            {
                Interlocked.Exchange(ref _attemptInFlight, 0);
            }
        }
    }

    private static InfoBarModel BuildModel()
    {
        int sampleCount = TryGetSampleCount();
        return new InfoBarModel(
            textSpans: new[] { new InfoBarTextSpan(BuildOnboardingMessage(sampleCount)) },
            actionItems: new[]
            {
                new InfoBarHyperlink("Open Macros window", OpenWindowActionContext),
                new InfoBarHyperlink("Don't show again", DismissActionContext),
            },
            image: KnownMonikers.StatusInformation,
            isCloseButtonVisible: true);
    }

    private static int TryGetSampleCount()
    {
        try
        {
            return new SampleTemplateProvider().GetTemplates().Count;
        }
        catch
        {
            // The provider reads embedded resource manifest entries — failures here would
            // indicate a packaging bug. Degrade to the count-free message instead of failing
            // the entire onboarding flow.
            return 0;
        }
    }

    private static async Task<InfoBar?> TryCreateInfoBarAsync(InfoBarModel model)
    {
        // Prefer the active document — InfoBars look most natural pinned to whatever the user is
        // currently looking at. Both VS.Documents.GetActiveDocumentViewAsync and the WindowFrame
        // property touch COM, so make sure we're on the UI thread before reaching for them.
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        try
        {
            DocumentView? docView = await VS.Documents.GetActiveDocumentViewAsync();
            if (docView?.WindowFrame is IVsWindowFrame frame)
            {
                InfoBar? hosted = await VS.InfoBar.CreateAsync(frame, model);
                if (hosted != null)
                {
                    return hosted;
                }
            }
        }
        catch (Exception ex)
        {
            await ex.LogAsync();
        }

        // Fall back to the shell's main-window InfoBar host (still anchored, just less local).
        try
        {
            return await VS.InfoBar.CreateAsync(model);
        }
        catch (Exception ex)
        {
            await ex.LogAsync();
            return null;
        }
    }

    private static void OnActionItemClicked(object sender, InfoBarActionItemEventArgs e)
    {
        // VS raises IVsInfoBarUIEvents on the UI thread; assert it so the threading analyzer is
        // happy reading IVsInfoBarActionItem properties below.
        ThreadHelper.ThrowIfNotOnUIThread();

        InfoBar? infoBar = sender as InfoBar;

        try
        {
            if (e.ActionItem.ActionContext is string context && context == OpenWindowActionContext)
            {
                // The Macros tool window comes from the parallel m1-tool-window-empty todo. Use
                // reflection so this file compiles and works whether or not that type exists yet.
                // TryShowMacrosToolWindowAsync switches to the UI thread itself, so we don't need
                // to wrap it in JoinableTaskFactory.RunAsync from this UI-thread callback.
                TryShowMacrosToolWindowAsync().Forget();
            }
        }
        catch (Exception ex)
        {
            _ = ex.LogAsync();
        }
        finally
        {
            // Either action item — and the close button — should dismiss the bar. The close button
            // is handled by VS itself; here we just close after the user clicks an action.
            try
            {
                infoBar?.Close();
            }
            catch
            {
                // Swallow — the bar is already going away.
            }
        }
    }

    private static async Task TryShowMacrosToolWindowAsync()
    {
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // TODO(m1-tool-window-empty): once MacrosToolWindow.ShowAsync ships, replace this
            // reflection probe with a direct static call.
            Assembly assembly = typeof(OnboardingInfoBar).Assembly;
            Type? toolWindowType =
                assembly.GetType("Macros.ToolWindows.MacrosToolWindow", throwOnError: false)
                ?? assembly.GetType("Macros.MacrosToolWindow", throwOnError: false);

            if (toolWindowType == null)
            {
                return;
            }

            MethodInfo? showMethod = toolWindowType.GetMethod(
                "ShowAsync",
                BindingFlags.Public | BindingFlags.Static);

            if (showMethod == null)
            {
                return;
            }

            if (showMethod.Invoke(null, parameters: null) is Task task)
            {
                await task;
            }
        }
        catch (Exception ex)
        {
            await ex.LogAsync();
        }
    }
}
