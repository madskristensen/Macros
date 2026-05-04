// Wired in MacrosPackage.InitializeAsync.

using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Macros.Options;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace Macros.Onboarding;

/// <summary>
/// One-shot InfoBar shown when the user opens a <c>.csx</c> macro file in the editor.
/// Reminds users that macros can be wired to VS events via Manage Triggers. Self-disables
/// after the first dismissal by flipping <see cref="MacrosOptions.HasShownTriggerHint"/>.
/// </summary>
/// <remarks>
/// <para>
/// Subscribes to <c>VS.Events.DocumentEvents.Opened</c> and filters by extension
/// (<c>.csx</c>) and path segment (<c>\Macros\</c>) via the pure
/// <see cref="IsMacroFilePath"/> helper, which is also the unit-under-test.
/// </para>
/// <para>
/// "Open Manage Triggers" is intentionally lightweight: since invoking the dialog
/// programmatically requires wiring up <c>MacroSelectionContext</c> and dispatching
/// through the command pipeline — complexity that is out of scope for this InfoBar — the
/// button instead surfaces a status-bar nudge directing the user to right-click in the
/// Macros tool window. The flag is still persisted so the bar never reappears.
/// </para>
/// </remarks>
internal sealed class TriggerHintInfoBar : IDisposable
{
    private const string OpenTriggersActionContext = "macros.triggerhint.open";
    private const string DismissActionContext = "macros.triggerhint.dismiss";

    private readonly AsyncPackage _package;
    private readonly JoinableTaskFactory _jtf;
    private DocumentEvents? _documentEvents;

    private TriggerHintInfoBar(AsyncPackage package)
    {
        _package = package ?? throw new ArgumentNullException(nameof(package));
        _jtf = package.JoinableTaskFactory;
    }

    /// <summary>
    /// Constructs the handler and subscribes to document-opened events. Must be awaited
    /// during package initialisation; switches to the main thread internally.
    /// </summary>
    public static async Task<TriggerHintInfoBar> InitializeAsync(AsyncPackage package)
    {
        _ = package ?? throw new ArgumentNullException(nameof(package));

        await package.JoinableTaskFactory.SwitchToMainThreadAsync();

        var hint = new TriggerHintInfoBar(package);
        var docEvents = VS.Events.DocumentEvents;
        docEvents.Opened += hint.OnDocumentOpened;
        hint._documentEvents = docEvents;
        return hint;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_documentEvents is not null)
        {
            try
            {
                _documentEvents.Opened -= OnDocumentOpened;
            }
            catch
            {
                // Shutdown path: event source may already be torn down.
            }

            _documentEvents = null;
        }
    }

    private void OnDocumentOpened(string filePath)
    {
        if (!IsMacroFilePath(filePath))
        {
            return;
        }

        _jtf.RunAsync(() => ShowIfNotSeenAsync(filePath))
            .FileAndForget("Macros/TriggerHint");
    }

    private async Task ShowIfNotSeenAsync(string filePath)
    {
        try
        {
            var options = await MacrosOptions.GetLiveInstanceAsync();
            if (options.HasShownTriggerHint)
            {
                return;
            }

            await _jtf.SwitchToMainThreadAsync();

            var model = new InfoBarModel(
                textSpans: new[] { new InfoBarTextSpan("Tip: This macro can run automatically on VS events. Configure via Manage Triggers...") },
                actionItems: new[]
                {
                    new InfoBarHyperlink("Open Manage Triggers", OpenTriggersActionContext),
                    new InfoBarHyperlink("Don't show again", DismissActionContext),
                },
                image: KnownMonikers.StatusInformation,
                isCloseButtonVisible: true);

            InfoBar? infoBar = await TryCreateInfoBarAsync(model, filePath);
            if (infoBar is null)
            {
                return;
            }

            infoBar.ActionItemClicked += OnActionItemClicked;

            bool shown = await infoBar.TryShowInfoBarUIAsync();
            if (!shown)
            {
                infoBar.ActionItemClicked -= OnActionItemClicked;
                return;
            }

            // Persist as soon as the bar is displayed — regardless of which action item
            // the user eventually clicks (or whether they just close it), we never show again.
            options.HasShownTriggerHint = true;
            await options.SaveAsync();
        }
        catch (Exception ex)
        {
            await ex.LogAsync();
        }
    }

    private void OnActionItemClicked(object sender, InfoBarActionItemEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var bar = sender as InfoBar;

        try
        {
            if (e.ActionItem.ActionContext is not string context)
            {
                return;
            }

            if (context == OpenTriggersActionContext)
            {
                // Direct programmatic dispatch of the Manage Triggers dialog requires
                // MacroSelectionContext to be populated and a command-pipeline call — too
                // much coupling for a one-shot hint bar. Nudge via the status bar instead.
                _jtf.RunAsync(async () =>
                    {
                        await VS.StatusBar.ShowMessageAsync(
                            "Right-click a macro in the Macros tool window → Manage Triggers...");
                    })
                    .FileAndForget("Macros/TriggerHint/StatusBar");
            }
            // DismissActionContext: no extra work — the flag was already persisted in ShowIfNotSeenAsync.
        }
        catch (Exception ex)
        {
            _ = ex.LogAsync();
        }
        finally
        {
            try
            {
                bar?.Close();
            }
            catch
            {
                // Bar may already be closing.
            }
        }
    }

    private static async Task<InfoBar?> TryCreateInfoBarAsync(InfoBarModel model, string filePath)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        // Anchor the hint to the frame hosting the just-opened .csx file. We can't rely on
        // the *active* document view here: at the moment DocumentEvents.Opened fires, the
        // active frame may still be something else (e.g. the Output window after a deploy),
        // which would cause the InfoBar to render in the wrong tool window.
        try
        {
            IVsWindowFrame? frame = await TryGetWindowFrameForPathAsync(filePath);
            if (frame is not null)
            {
                InfoBar? hosted = await VS.InfoBar.CreateAsync(frame, model);
                if (hosted is not null)
                {
                    return hosted;
                }
            }
        }
        catch (Exception ex)
        {
            await ex.LogAsync();
        }

        // Fall back to the shell's main InfoBar host.
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

    private static async Task<IVsWindowFrame?> TryGetWindowFrameForPathAsync(string filePath)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var openDoc = await VS.GetServiceAsync<SVsUIShellOpenDocument, IVsUIShellOpenDocument>();
        if (openDoc is null)
        {
            return null;
        }

        var logicalView = VSConstants.LOGVIEWID_Primary;
        int hr = openDoc.IsDocumentOpen(
            null,
            (uint)VSConstants.VSITEMID.Nil,
            filePath,
            ref logicalView,
            (uint)__VSIDOFLAGS.IDO_IgnoreLogicalView,
            out _,
            null,
            out IVsWindowFrame frame,
            out int isOpen);

        if (Microsoft.VisualStudio.ErrorHandler.Succeeded(hr) && isOpen != 0)
        {
            return frame;
        }

        return null;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="path"/> points to a
    /// <c>.csx</c> file located inside a <c>\Macros\</c> directory segment — the
    /// conventional location for both global (<c>%APPDATA%\Macros\</c>) and repo-scoped
    /// (<c>.vs\Macros\</c>) macros. False positives from non-macro <c>.csx</c> files in
    /// unrelated folders are intentionally accepted rather than adding fragile path
    /// heuristics; the worst outcome is a benign one-time InfoBar.
    /// </summary>
    internal static bool IsMacroFilePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        if (!path.EndsWith(".csx", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return path.IndexOf(@"\Macros\", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
