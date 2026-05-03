using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Macros.Engine.Player;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

namespace Macros.Errors;

/// <summary>
/// Routes a failed <see cref="MacroPlayResult"/> to the three Visual Studio surfaces a user
/// expects to see when something breaks: the <b>Macros Output pane</b> (full diagnostic text),
/// the <b>Error List</b> (one entry per compile diagnostic with line/column), and an
/// <b>InfoBar</b> (succinct "Macro failed — View output" pinned to the active document).
/// </summary>
/// <remarks>
/// <para>
/// Static-only by design: there is no per-instance state worth keeping, but a single
/// process-wide <see cref="ErrorListProvider"/> is reused across calls so the Error List
/// shows a clean replacement each run instead of accumulating stale entries.
/// </para>
/// <para>
/// All three surfaces are touched on the UI thread; <see cref="OutputWindowPane"/>'s
/// <c>WriteLineAsync</c> and <see cref="InfoBar.TryShowInfoBarUIAsync"/> handle the marshalling
/// internally, but Error List APIs require an explicit <see cref="ThreadHelper.ThrowIfNotOnUIThread"/>
/// — we switch to the main thread before touching them.
/// </para>
/// <para>
/// Failures inside the renderer itself are swallowed and logged: a broken renderer must never
/// mask the original macro failure or crash the package. Coverage at the unit-test level is
/// limited because Output / Error List / InfoBar all require a hosted VS process; the integration
/// tests in <c>Macros.IntegrationTests</c> exercise the full path inside an experimental hive.
/// </para>
/// </remarks>
internal static class MacroErrorRenderer
{
    private const string OutputPaneName = "Macros";
    private const string ViewOutputActionContext = "macros.error.viewOutput";

    // Roslyn diagnostic ToString() format is "(line,col): severity CSXXXX: message" for script
    // diagnostics that have no file name. Capture all four parts so we can lift them into the
    // Error List with proper navigation hints. Group order matches Roslyn's emit order.
    private static readonly Regex DiagnosticPattern = new(
        @"^\((?<line>\d+),(?<col>\d+)\):\s*(?<sev>error|warning|info)\s+(?<code>[A-Za-z]+\d+):\s*(?<msg>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly object _providerLock = new();
    private static ErrorListProvider? _provider;

    /// <summary>
    /// Surfaces the failure described by <paramref name="result"/> to the user. A
    /// <see cref="MacroPlayResult"/> with <c>Success == true</c> is a no-op and returns
    /// immediately without touching any VS surface.
    /// </summary>
    /// <param name="result">The playback result to surface; success results are ignored.</param>
    /// <param name="macroName">
    /// Friendly name used in the Output heading, Error List "Source" column, and InfoBar text.
    /// </param>
    public static async Task RenderAsync(MacroPlayResult result, string macroName)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));
        if (macroName is null) throw new ArgumentNullException(nameof(macroName));
        if (result.Success) return;

        // Cancellation is not an error — suppress InfoBar and Error List; show a brief
        // status bar message and a single Output line so the user knows why playback stopped.
        if (result.RuntimeError is OperationCanceledException)
        {
            try
            {
                OutputWindowPane pane = await GetOrCreatePaneAsync().ConfigureAwait(true);
                await pane.WriteLineAsync("Macro cancelled by user.").ConfigureAwait(true);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
#pragma warning disable CS0618 // ServiceProvider.GlobalProvider is the accepted access pattern from a static helper.
                if (ServiceProvider.GlobalProvider.GetService(typeof(SVsStatusbar)) is IVsStatusbar bar)
                {
                    bar.SetText("Macros: Replay cancelled");
                }
#pragma warning restore CS0618
            }
            catch (Exception ex)
            {
                await ex.LogAsync().ConfigureAwait(false);
            }

            return;
        }

        try
        {
            OutputWindowPane pane = await GetOrCreatePaneAsync().ConfigureAwait(true);
            await WriteDiagnosticTextAsync(pane, result, macroName).ConfigureAwait(true);
            await pane.ActivateAsync().ConfigureAwait(true);

            await PublishToErrorListAsync(result, macroName).ConfigureAwait(true);
            await ShowInfoBarAsync(pane, macroName).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Renderer must never throw back into the caller — the original failure has higher
            // signal value than a render error. Best-effort log; the caller is the Play command,
            // which has already rolled state back to Idle by the time we get here.
            await ex.LogAsync().ConfigureAwait(false);
        }
    }

    private static async Task<OutputWindowPane> GetOrCreatePaneAsync()
    {
        // CreateOutputWindowPaneAsync is idempotent: VS reuses the same pane when the name
        // matches. We deliberately don't cache the OutputWindowPane instance because the
        // toolkit's helper short-cuts to GetAsync(Guid) when the pane already exists, and
        // re-resolving each call is microseconds compared to the user-facing IO that follows.
        return await VS.Windows.CreateOutputWindowPaneAsync(OutputPaneName, lazyCreate: false);
    }

    private static async Task WriteDiagnosticTextAsync(OutputWindowPane pane, MacroPlayResult result, string macroName)
    {
        await pane.WriteLineAsync(
            $"=== {macroName} failed in {result.Duration.TotalMilliseconds:F0} ms ===");

        if (!string.IsNullOrEmpty(result.CompilationError))
        {
            await pane.WriteLineAsync("Compilation errors:");
            await pane.WriteLineAsync(result.CompilationError ?? string.Empty);
        }

        if (result.RuntimeError is not null)
        {
            await pane.WriteLineAsync("Runtime exception:");
            await pane.WriteLineAsync(result.RuntimeError.ToString());
        }

        // Trailing blank line keeps successive failures visually separated when the user
        // re-runs a broken macro repeatedly. Cheap, and matches MSBuild output conventions.
        await pane.WriteLineAsync(string.Empty);
    }

    private static async Task PublishToErrorListAsync(MacroPlayResult result, string macroName)
    {
        if (string.IsNullOrEmpty(result.CompilationError))
        {
            // Runtime failures don't have line/col; we leave them in the Output pane only.
            // Adding a single "Runtime exception" Error List entry would dilute the signal —
            // double-click navigation has nowhere useful to land.
            await ClearErrorListAsync(macroName).ConfigureAwait(true);
            return;
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        ErrorListProvider provider = GetProvider();
        provider.SuspendRefresh();
        try
        {
            ClearOurTasks(provider);

            string documentLabel = $"{macroName}.csx";
            foreach (string line in result.CompilationError!.Split('\n'))
            {
                string trimmed = line.TrimEnd('\r').Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                ErrorTask task = ParseDiagnostic(trimmed, documentLabel, macroName);
                provider.Tasks.Add(task);
            }

            provider.Show();
        }
        finally
        {
            provider.ResumeRefresh();
        }
    }

    private static async Task ClearErrorListAsync(string macroName)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        ErrorListProvider provider = GetProvider();
        ClearOurTasks(provider);
    }

    private static void ClearOurTasks(ErrorListProvider provider)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // Tasks.Clear() would nuke entries published by other extensions sharing the provider —
        // ours always go through this method, so we own and remove them by reference.
        var ours = new List<ErrorTask>();
        foreach (object? task in provider.Tasks)
        {
            if (task is ErrorTask et && string.Equals(et.Category.ToString(), nameof(TaskCategory.User), StringComparison.Ordinal))
            {
                // Heuristic: we tag every task we publish with TaskCategory.User. Fine for MVP.
                ours.Add(et);
            }
        }

        foreach (ErrorTask t in ours)
        {
            provider.Tasks.Remove(t);
        }
    }

    private static ErrorTask ParseDiagnostic(string line, string documentLabel, string macroName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        Match m = DiagnosticPattern.Match(line);
        var task = new ErrorTask
        {
            Category = TaskCategory.User,
            ErrorCategory = TaskErrorCategory.Error,
            Document = documentLabel,
            HierarchyItem = null,
        };

        if (m.Success)
        {
            // Roslyn lines/cols are 1-based; ErrorTask is 0-based for both axes.
            int.TryParse(m.Groups["line"].Value, out int oneBasedLine);
            int.TryParse(m.Groups["col"].Value, out int oneBasedCol);
            task.Line = Math.Max(0, oneBasedLine - 1);
            task.Column = Math.Max(0, oneBasedCol - 1);

            string sev = m.Groups["sev"].Value;
            task.ErrorCategory = sev.Equals("warning", StringComparison.OrdinalIgnoreCase)
                ? TaskErrorCategory.Warning
                : TaskErrorCategory.Error;

            task.Text = $"{m.Groups["code"].Value}: {m.Groups["msg"].Value}";
        }
        else
        {
            // Unparseable line: still surface it so the user sees something rather than silent
            // dropping. Line/Column default to 0 which navigates to the top of the document.
            task.Text = line;
        }

        // Source label — appears in the Error List "Project" column. "Macros" plus the name
        // gives the user a stable, scannable identifier.
        task.HelpKeyword = $"Macros:{macroName}";
        return task;
    }

    private static ErrorListProvider GetProvider()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_provider is not null) return _provider;

        lock (_providerLock)
        {
            if (_provider is not null) return _provider;

#pragma warning disable CS0618 // ServiceProvider.GlobalProvider is the supported access pattern from a static helper.
            _provider = new ErrorListProvider(ServiceProvider.GlobalProvider)
            {
                ProviderName = "Macros",
                ProviderGuid = new Guid("8c6b0d51-3f5a-4f14-9f72-1b2dc9b6d701"),
            };
#pragma warning restore CS0618
            return _provider;
        }
    }

    private static async Task ShowInfoBarAsync(OutputWindowPane pane, string macroName)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var model = new InfoBarModel(
            textSpans: new[]
            {
                new InfoBarTextSpan($"Macro \"{macroName}\" failed. "),
            },
            actionItems: new[]
            {
                new InfoBarHyperlink("View Output", ViewOutputActionContext),
            },
            image: KnownMonikers.StatusError,
            isCloseButtonVisible: true);

        InfoBar? infoBar = await TryCreateInfoBarAsync(model).ConfigureAwait(true);
        if (infoBar is null) return;

        // Capture the pane so the action handler can re-activate it without re-resolving.
        // The handler runs on the UI thread (VS contract for IVsInfoBarUIEvents), but we
        // still post the activate via the JoinableTaskFactory so async failures don't
        // leak back into the COM callback.
        infoBar.ActionItemClicked += (sender, e) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (e.ActionItem.ActionContext is string ctx && ctx == ViewOutputActionContext)
                {
                    ActivatePaneSafelyAsync(pane).Forget();
                }
            }
            finally
            {
                try { (sender as InfoBar)?.Close(); } catch { /* already closing */ }
            }
        };

        try
        {
            await infoBar.TryShowInfoBarUIAsync();
        }
        catch (Exception ex)
        {
            await ex.LogAsync();
        }
    }

    private static async Task ActivatePaneSafelyAsync(OutputWindowPane pane)
    {
        try
        {
            // OutputWindowPane.ActivateAsync only switches to our pane WITHIN the Output
            // window — it does nothing if the Output tool window itself is hidden. Execute
            // View.Output first so the window is visible, then activate our pane on top.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                await VS.Commands.ExecuteAsync("View.Output");
            }
            catch
            {
                // The view command isn't strictly required — fall through to the pane
                // activation. If the user already has the Output window open this is a
                // no-op anyway.
            }

            await pane.ActivateAsync();
        }
        catch (Exception ex)
        {
            await ex.LogAsync();
        }
    }

    private static async Task<InfoBar?> TryCreateInfoBarAsync(InfoBarModel model)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        // Prefer the active document so the bar appears next to whatever the user just tried
        // to play the macro against. Falls through to the shell-wide host on the second try.
        try
        {
            DocumentView? docView = await VS.Documents.GetActiveDocumentViewAsync();
            if (docView?.WindowFrame is IVsWindowFrame frame)
            {
                InfoBar? hosted = await VS.InfoBar.CreateAsync(frame, model);
                if (hosted is not null) return hosted;
            }
        }
        catch (Exception ex)
        {
            await ex.LogAsync();
        }

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
}
