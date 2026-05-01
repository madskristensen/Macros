using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Macros.Engine.Storage;
using Macros.Lifecycle;
using Macros.Options;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace Macros.Trust;

/// <summary>
/// Shows a trust-gate InfoBar at the top of the editor whenever a solution opens that
/// contains repo-scoped macros with auto-run triggers and the user has neither trusted
/// nor blocked the solution yet. Triggers stay dormant until the user makes a choice
/// (see <see cref="MacrosOptions.IsSolutionTrusted"/> consumers in the trigger registry).
/// </summary>
/// <remarks>
/// <para>
/// Lifecycle: constructed by <c>MacrosPackage</c> after the
/// <see cref="SolutionContextTracker"/> has been initialised, then owned on a package
/// field for the duration of the VS session. Disposing the bar unsubscribes from the
/// tracker and closes any active InfoBar so a stale handle can't outlive the package.
/// </para>
/// <para>
/// Decision policy lives in <see cref="TrustGateLogic.ShouldShow"/> so it can be unit
/// tested without standing up the shell. This class is the thin VS-side wrapper that
/// resolves the inputs (current solution, trust state, repo macros), shows the bar,
/// and routes action-item clicks back into <see cref="MacrosOptions"/> /
/// <see cref="VS.Commands"/>.
/// </para>
/// </remarks>
internal sealed class TrustGateInfoBar : IDisposable
{
    private const string TrustActionContext = "macros.trust.trust";
    private const string BlockActionContext = "macros.trust.block";
    private const string ManageActionContext = "macros.trust.manage";
    private const string DismissActionContext = "macros.trust.dismiss";

    private readonly SolutionContextTracker _tracker;
    private readonly AsyncPackage _package;
    private readonly JoinableTaskFactory _jtf;

    private InfoBar? _activeBar;
    private string? _activeSolutionPath;

    private TrustGateInfoBar(AsyncPackage package, SolutionContextTracker tracker)
    {
        _package = package ?? throw new ArgumentNullException(nameof(package));
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _jtf = package.JoinableTaskFactory;
    }

    /// <summary>
    /// Constructs the handler, subscribes to <see cref="SolutionContextTracker.SolutionChanged"/>,
    /// and runs an initial check against the currently-open solution (if any). Must be
    /// awaited from package init on or after the tracker is wired.
    /// </summary>
    public static async Task<TrustGateInfoBar> InitializeAsync(AsyncPackage package, SolutionContextTracker tracker)
    {
        _ = package ?? throw new ArgumentNullException(nameof(package));
        _ = tracker ?? throw new ArgumentNullException(nameof(tracker));

        var bar = new TrustGateInfoBar(package, tracker);
        tracker.SolutionChanged += bar.OnSolutionChanged;
        await bar.CheckAndShowAsync();
        return bar;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _tracker.SolutionChanged -= OnSolutionChanged;
        HideActiveBar();
    }

    private void OnSolutionChanged(object? sender, EventArgs e)
    {
        // The tracker fires synchronously on whichever thread the toolkit raised the
        // open / close event; hop onto a JTF task so the InfoBar work runs cleanly off
        // the event source and we can switch to the UI thread on demand.
        _jtf.RunAsync(CheckAndShowAsync).FileAndForget("Macros/TrustGate");
    }

    private async Task CheckAndShowAsync()
    {
        try
        {
            var solutionPath = _tracker.GetCurrentSolutionDirectory();

            // Always tear down a stale bar before deciding whether a new one is warranted —
            // switching solutions must not leave the prior solution's InfoBar visible.
            await _jtf.SwitchToMainThreadAsync();
            if (_activeBar is not null && !string.Equals(_activeSolutionPath, solutionPath, StringComparison.OrdinalIgnoreCase))
            {
                HideActiveBar();
            }

            if (solutionPath is null)
            {
                return;
            }

            var options = await MacrosOptions.GetLiveInstanceAsync();
            var isTrusted = options.IsSolutionTrusted(solutionPath);
            var isBlocked = options.IsSolutionBlocked(solutionPath);

            if (isTrusted || isBlocked)
            {
                return;
            }

            var repoMacros = await TryListRepoMacrosAsync();
            var (show, triggeredCount) = TrustGateLogic.ShouldShow(solutionPath, isTrusted, isBlocked, repoMacros);
            if (!show)
            {
                return;
            }

            // Avoid double-showing if a previous CheckAndShowAsync already surfaced a bar
            // for the same solution (e.g. tracker fires twice during open).
            if (_activeBar is not null && string.Equals(_activeSolutionPath, solutionPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await ShowBarAsync(solutionPath, triggeredCount);
        }
        catch (Exception ex)
        {
            await ex.LogAsync();
        }
    }

    private static async Task<System.Collections.Generic.IReadOnlyList<MacroEntry>> TryListRepoMacrosAsync()
    {
        try
        {
            var store = await VS.GetRequiredServiceAsync<IMacroStore, IMacroStore>();
            return await store.ListAsync(MacroScope.Repo);
        }
        catch
        {
            // Repo scope throws when no solution folder is resolvable; treat as "no
            // macros" rather than letting the failure surface as an error InfoBar.
            return Array.Empty<MacroEntry>();
        }
    }

    private async Task ShowBarAsync(string solutionPath, int triggeredCount)
    {
        await _jtf.SwitchToMainThreadAsync();

        var message = $"This solution contains {triggeredCount} macro(s) with auto-run triggers. They are blocked until you trust this solution.";

        var model = new InfoBarModel(
            textSpans: new[] { new InfoBarTextSpan(message) },
            actionItems: new[]
            {
                new InfoBarHyperlink("Trust", TrustActionContext),
                new InfoBarHyperlink("Block", BlockActionContext),
                new InfoBarHyperlink("Manage...", ManageActionContext),
                new InfoBarHyperlink("Dismiss", DismissActionContext),
            },
            image: KnownMonikers.StatusWarning,
            isCloseButtonVisible: true);

        InfoBar? infoBar = await TryCreateInfoBarAsync(model);
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

        _activeBar = infoBar;
        _activeSolutionPath = solutionPath;
    }

    private static async Task<InfoBar?> TryCreateInfoBarAsync(InfoBarModel model)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        try
        {
            DocumentView? docView = await VS.Documents.GetActiveDocumentViewAsync();
            if (docView?.WindowFrame is IVsWindowFrame frame)
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

    private void OnActionItemClicked(object sender, InfoBarActionItemEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var bar = sender as InfoBar;
        var solutionPath = _activeSolutionPath;

        try
        {
            if (e.ActionItem.ActionContext is not string context)
            {
                return;
            }

            switch (context)
            {
                case TrustActionContext when solutionPath is not null:
                    _jtf.RunAsync(async () =>
                    {
                        var opts = await MacrosOptions.GetLiveInstanceAsync();
                        opts.TrustSolution(solutionPath);
                        await opts.SaveAsync();
                    }).FileAndForget("Macros/TrustGate/Trust");
                    break;

                case BlockActionContext when solutionPath is not null:
                    _jtf.RunAsync(async () =>
                    {
                        var opts = await MacrosOptions.GetLiveInstanceAsync();
                        opts.BlockSolution(solutionPath);
                        await opts.SaveAsync();
                    }).FileAndForget("Macros/TrustGate/Block");
                    break;

                case ManageActionContext:
                    _jtf.RunAsync(async () =>
                    {
                        await _jtf.SwitchToMainThreadAsync();
                        try
                        {
                            // Open the dedicated Trusted Solutions options page directly so the
                            // user lands on the right node without hunting for the sub-category.
                            _package.ShowOptionPage(typeof(TrustedSolutionsPage));
                        }
                        catch (Exception ex)
                        {
                            await ex.LogAsync();
                        }
                    }).FileAndForget("Macros/TrustGate/Manage");
                    break;

                case DismissActionContext:
                    // No persisted state — closing the bar below is the entire effect.
                    break;
            }
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
                // Bar already closed — ignore.
            }

            if (ReferenceEquals(bar, _activeBar))
            {
                _activeBar = null;
                _activeSolutionPath = null;
            }
        }
    }

    private void HideActiveBar()
    {
        var bar = _activeBar;
        _activeBar = null;
        _activeSolutionPath = null;
        if (bar is null)
        {
            return;
        }

        try
        {
            bar.ActionItemClicked -= OnActionItemClicked;
        }
        catch
        {
            // Event source already torn down — no action available.
        }

        try
        {
            bar.Close();
        }
        catch
        {
            // Already closed by the shell.
        }
    }
}
