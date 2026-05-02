using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Community.VisualStudio.Toolkit;
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
    /// Double-clicking a macro row triggers the same default action as Enter: play the macro.
    /// </summary>
    private void MacroRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!(sender is FrameworkElement fe) || !(fe.DataContext is MacroItemViewModel item))
        {
            return;
        }

        // Ignore double-clicks that originate from the inline Play button itself.
        if (e.OriginalSource is DependencyObject origin && FindAncestor<ButtonBase>(origin) is not null)
        {
            return;
        }

        if (item.PlayCommand.CanExecute(null))
        {
            item.PlayCommand.Execute(null);
            e.Handled = true;
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

    /// <summary>
    /// Keyboard alternative to right-click: pressing the Apps (context menu) key or
    /// Shift+F10 on a focused macro row opens the same VSCT context menu the mouse handler
    /// shows. Without this, screen-reader and keyboard-only users would have no way to
    /// reach the per-row Edit / Rename / Delete commands the M3 wave wired up.
    ///
    /// Also routes the row's "default action" keys directly to their commands:
    ///   * Enter -> Play   (matches the VSCT KeyBinding gesture text on the menu item)
    ///   * F7    -> Edit   (matches the VSCT KeyBinding gesture text on the menu item)
    ///   * F2    -> Rename (matches the Solution Explorer / file rename convention)
    /// We invoke the commands here rather than relying on the VSCT KeyBinding alone
    /// because the WPF ListView swallows Enter/F7/F2 before the IDE keyboard chain sees them.
    /// </summary>
    private void MacroRow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!(sender is FrameworkElement fe) || !(fe.DataContext is MacroItemViewModel item))
        {
            return;
        }

        bool isEnter = e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None;
        bool isF7 = e.Key == Key.F7 && Keyboard.Modifiers == ModifierKeys.None;
        bool isF2 = e.Key == Key.F2 && Keyboard.Modifiers == ModifierKeys.None;
        bool isContextMenu = e.Key == Key.Apps
            || (e.Key == Key.F10 && (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift);

        if (!isEnter && !isF7 && !isF2 && !isContextMenu)
        {
            return;
        }

        ThreadHelper.ThrowIfNotOnUIThread();

        // Enter -> Play the selected macro (default row action).
        if (isEnter)
        {
            MacroSelectionContext.Current = item.Descriptor;
            ExecuteContextCommand(PackageIds.cmdidMacrosCtxPlay);
            e.Handled = true;
            return;
        }

        // F7 -> Edit the selected macro.
        if (isF7)
        {
            MacroSelectionContext.Current = item.Descriptor;
            ExecuteContextCommand(PackageIds.cmdidMacrosCtxEdit);
            e.Handled = true;
            return;
        }

        // F2 -> Rename the selected macro.
        if (isF2)
        {
            MacroSelectionContext.Current = item.Descriptor;
            ExecuteContextCommand(PackageIds.cmdidMacrosCtxRename);
            e.Handled = true;
            return;
        }

        MacroSelectionContext.Current = item.Descriptor;
        try
        {
            // Anchor the menu to the bottom-left of the focused row so it appears in a
            // sensible spot relative to the keyboard's "current item" rather than the
            // last mouse position.
            Point screen = fe.PointToScreen(new Point(0, fe.ActualHeight));
            ShowContextMenuAtScreen(screen);
        }
        catch (Exception)
        {
            // Same defensive swallow as the mouse handler — a missing IVsUIShell during
            // hosted tests must not crash the tool window.
        }

        e.Handled = true;
    }

    private static void ExecuteContextCommand(int commandId)
    {
        // Fire-and-forget: VS.Commands.ExecuteAsync hops to the UI thread internally and
        // returns once the command target accepts the invocation. Failures (no handler,
        // command unavailable) must never crash the tool window, so the continuation
        // logs and swallows.
        _ = VS.Commands.ExecuteAsync(PackageGuids.guidMacrosPackageCmdSet, commandId)
            .ContinueWith(t =>
            {
                if (t.Exception is not null)
                {
                    _ = t.Exception.LogAsync();
                }
            }, System.Threading.Tasks.TaskScheduler.Default);
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

        Guid cmdSetGuid = PackageGuids.guidMacrosPackageCmdSet;
        uiShell.ShowContextMenu(
            dwCompRole: 0,
            rclsidActive: ref cmdSetGuid,
            nMenuId: PackageIds.MacrosToolWindowContextMenu,
            pos: pts,
            pCmdTrgtActive: null);
    }

    private static T? FindAncestor<T>(DependencyObject? start)
        where T : DependencyObject
    {
        DependencyObject? current = start;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
