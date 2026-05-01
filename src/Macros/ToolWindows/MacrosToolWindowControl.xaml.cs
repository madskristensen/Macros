using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Macros.Commands.Context;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Macros.ToolWindows;

/// <summary>
/// Code-behind for the Macros tool window WPF content. The class is deliberately a thin shell:
/// all behaviour lives in <see cref="MacrosToolWindowViewModel"/> and is bound from XAML. The
/// only responsibilities here are wiring an injected view-model (production path through
/// <see cref="MacrosToolWindow.CreateAsync"/>) and disposing it when the panel is unloaded so
/// the engine / storage event subscriptions don't leak across tool-window close-and-reopen.
/// </summary>
public partial class MacrosToolWindowControl : UserControl
{
    /// <summary>
    /// Initializes the control with no view-model. Used by the XAML designer and by the
    /// fallback path when <see cref="MacrosToolWindow"/> hasn't been able to construct a
    /// view-model yet.
    /// </summary>
    public MacrosToolWindowControl()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Initializes the control with a pre-built <paramref name="viewModel"/> bound as the
    /// data context. Production callers (<see cref="MacrosToolWindow.CreateAsync"/>) use
    /// this overload so the panel renders populated content on first show.
    /// </summary>
    /// <param name="viewModel">The view-model to bind. Required.</param>
    /// <exception cref="ArgumentNullException"><paramref name="viewModel"/> is <see langword="null"/>.</exception>
    public MacrosToolWindowControl(MacrosToolWindowViewModel viewModel)
        : this()
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        // Dispose the VM if we own it. Disposing here (rather than in the pane) ensures the
        // storage / service event handlers are detached even if VS recycles the WPF tree
        // without fully closing the tool window pane.
        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    /// <summary>
    /// Right-click on a macro row: capture the targeted macro into
    /// <see cref="MacroSelectionContext"/> so the VSCT-defined context menu's commands can
    /// resolve their selection, then ask <see cref="IVsUIShell"/> to display the menu at
    /// the current cursor position.
    /// </summary>
    /// <remarks>
    /// We use <see cref="UIElement.PreviewMouseRightButtonUp"/> rather than the bubbling
    /// MouseRightButtonUp because the WPF Border + Expander tree above us occasionally
    /// swallows the bubbling event. The VSCT context menu is shown via
    /// <see cref="IVsUIShell.ShowContextMenu"/> instead of a WPF <c>ContextMenu</c> so it
    /// inherits VS theming and command routing for free.
    /// </remarks>
    private void MacroRow_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (sender is FrameworkElement fe && fe.DataContext is MacroItemViewModel item)
        {
            MacroSelectionContext.Current = item.Descriptor;
            try
            {
                // Translate the click position to screen coordinates; PointToScreen returns
                // physical pixels on .NET Framework WPF, which is exactly what ShowContextMenu
                // expects for its POINTS argument.
                Point screen = fe.PointToScreen(e.GetPosition(fe));
                ShowContextMenuAtScreen(screen);
            }
            catch (Exception)
            {
                // Showing the menu must never crash the tool window. The most likely failure
                // mode is the IVsUIShell service being unavailable in design-time / hosted
                // test scenarios; silently bow out.
            }

            // Mark handled so the bubbling RightButtonUp doesn't also try to open a default
            // (un-themed) WPF context menu on a parent element.
            e.Handled = true;
        }
    }

    private static void ShowContextMenuAtScreen(Point screen)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var uiShell = Package.GetGlobalService(typeof(SVsUIShell)) as IVsUIShell;
        if (uiShell is null)
        {
            return;
        }

        var pts = new[] { new POINTS { x = (short)screen.X, y = (short)screen.Y } };

        Guid cmdSetGuid = new(PackageGuids.CommandSetGuidString);
        uiShell.ShowContextMenu(
            dwCompRole: 0,
            rclsidActive: ref cmdSetGuid,
            nMenuId: PackageIds.MacrosToolWindowContextMenu,
            pos: pts,
            pCmdTrgtActive: null);
    }
}
