// Wired in MacrosPackage.InitializeAsync.

using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Macros.ToolWindows;

/// <summary>
/// Placeholder Macros tool window registered in M1. The empty-state UI shipped here is replaced
/// in M3 with the real macro list (search box, themed ListView, action bar) per the UX
/// architecture doc.
/// </summary>
/// <remarks>
/// <para>
/// Uses the Community Toolkit <see cref="BaseToolWindow{T}"/> pattern: the outer class owns
/// the title and the WPF content factory; the nested <see cref="Pane"/> class is the actual
/// <c>ToolWindowPane</c> VS instantiates and is what package attributes reference via
/// <c>typeof(MacrosToolWindow.Pane)</c>.
/// </para>
/// </remarks>
public sealed class MacrosToolWindow : BaseToolWindow<MacrosToolWindow>
{
    /// <inheritdoc />
    public override string GetTitle(int toolWindowId) => "Macros";

    /// <inheritdoc />
    public override Type PaneType => typeof(Pane);

    /// <inheritdoc />
    public override async Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken)
    {
        // Best-effort: build the M3 view-model with services from the package container. If
        // anything in the resolve / initial-load path throws (e.g. running against a stripped
        // shell during diagnostics), we still surface the empty control so the tool window
        // can at least open. The view-model surfaces its own errors via the inline banner.
        try
        {
            var viewModel = await MacrosToolWindowViewModel.CreateAsync();
            return new MacrosToolWindowControl(viewModel);
        }
        catch (Exception ex)
        {
            await ex.LogAsync();
            return new MacrosToolWindowControl();
        }
    }

    /// <summary>
    /// The actual VS tool window pane. Kept nested + internal because it's an implementation
    /// detail of the Toolkit registration; consumers reference <see cref="MacrosToolWindow"/>.
    /// </summary>
    [Guid("a4c1b2d8-3e5f-4a6b-9c7d-8e0f1a2b3c4d")]
    internal sealed class Pane : ToolkitToolWindowPane
    {
        public Pane()
        {
            // Generic Record icon stands in until M5 polish swaps it for a custom Macros moniker.
            BitmapImageMoniker = KnownMonikers.Record;

            // Wire the VSCT-defined tool window toolbar (Record / Stop / Refresh).
            ToolBar = new CommandID(PackageGuids.guidMacrosPackageCmdSet, PackageIds.MacrosToolWindowToolbar);
        }

        // ── Native VS search ────────────────────────────────────────────────────────────

        /// <summary>Enables the native VS search bar in the tool window chrome.</summary>
        public override bool SearchEnabled => true;

        /// <inheritdoc />
        public override IVsSearchTask CreateSearch(
            uint dwCookie,
            IVsSearchQuery pSearchQuery,
            IVsSearchCallback pSearchCallback)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (pSearchQuery is null || pSearchCallback is null)
                return null!;

            // Capture the search string on the UI thread to avoid accessing the COM
            // IVsSearchQuery interface from the background thread in OnStartSearch.
            string searchString = pSearchQuery.SearchString;

            var vm = GetViewModel();
            if (vm is null)
                return null!;

            return new MacroSearchTask(dwCookie, pSearchQuery, pSearchCallback, vm, searchString);
        }

        /// <inheritdoc />
        public override void ClearSearch()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            GetViewModel()?.SetFilter(null);
        }

        private MacrosToolWindowViewModel? GetViewModel()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return (Content as MacrosToolWindowControl)?.DataContext as MacrosToolWindowViewModel;
        }

        // ── Search task ─────────────────────────────────────────────────────────────────

        private sealed class MacroSearchTask : VsSearchTask
        {
            private readonly MacrosToolWindowViewModel _vm;
            private readonly string _searchString;

            public MacroSearchTask(
                uint dwCookie,
                IVsSearchQuery pSearchQuery,
                IVsSearchCallback pSearchCallback,
                MacrosToolWindowViewModel vm,
                string searchString)
                : base(dwCookie, pSearchQuery, pSearchCallback)
            {
                _vm = vm;
                _searchString = searchString;
            }

            /// <summary>
            /// Runs on a background thread. Pushes the query string to the VM filter and
            /// reports completion. The VM filter is synchronous so no progress reporting is
            /// needed; we switch to the main thread for the ObservableCollection mutation.
            /// </summary>
            protected override void OnStartSearch()
            {
                ErrorCode = VSConstants.S_OK;
                string filter = _searchString;

                ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    _vm.SetFilter(filter);
                });

                SearchResults = 1;
                base.OnStartSearch();
            }

            protected override void OnStopSearch()
            {
                SearchResults = 0;
            }
        }
    }
}

