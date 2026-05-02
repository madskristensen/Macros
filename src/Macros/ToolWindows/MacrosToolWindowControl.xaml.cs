using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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
        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private void MacroRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!(sender is FrameworkElement fe) || !(fe.DataContext is MacroItemViewModel item))
        {
            return;
        }

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

    private void SampleRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is SampleTemplateItemViewModel item && item.OpenCommand.CanExecute(null))
        {
            item.OpenCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void SampleRow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        if (sender is FrameworkElement fe && fe.DataContext is SampleTemplateItemViewModel item && item.OpenCommand.CanExecute(null))
        {
            item.OpenCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void MacroRow_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (sender is FrameworkElement fe && fe.DataContext is MacroItemViewModel item)
        {
            MacroSelectionContext.Current = item.Descriptor;
            try
            {
                Point screen = fe.PointToScreen(e.GetPosition(fe));
                ShowContextMenuAtScreen(PackageIds.MacrosToolWindowContextMenu, screen);
            }
            catch (Exception)
            {
            }

            e.Handled = true;
        }
    }

    private void MacroGroupHeader_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!(sender is FrameworkElement fe) || !TrySetMacroGroupSelection(fe.DataContext))
        {
            return;
        }

        try
        {
            Point screen = fe.PointToScreen(e.GetPosition(fe));
            ShowContextMenuAtScreen(PackageIds.MacrosToolWindowGroupContextMenu, screen);
        }
        catch (Exception)
        {
        }

        e.Handled = true;
    }

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

        if (isEnter)
        {
            MacroSelectionContext.Current = item.Descriptor;
            ExecuteContextCommand(PackageIds.cmdidMacrosCtxPlay);
            e.Handled = true;
            return;
        }

        if (isF7)
        {
            MacroSelectionContext.Current = item.Descriptor;
            ExecuteContextCommand(PackageIds.cmdidMacrosCtxEdit);
            e.Handled = true;
            return;
        }

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
            Point screen = fe.PointToScreen(new Point(0, fe.ActualHeight));
            ShowContextMenuAtScreen(PackageIds.MacrosToolWindowContextMenu, screen);
        }
        catch (Exception)
        {
        }

        e.Handled = true;
    }

    private static void ExecuteContextCommand(int commandId)
    {
        _ = VS.Commands.ExecuteAsync(PackageGuids.guidMacrosPackageCmdSet, commandId)
            .ContinueWith(t =>
            {
                if (t.Exception is not null)
                {
                    _ = t.Exception.LogAsync();
                }
            }, System.Threading.Tasks.TaskScheduler.Default);
    }

    private static void ShowContextMenuAtScreen(int menuId, Point screen)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var uiShell = Package.GetGlobalService(typeof(SVsUIShell)) as IVsUIShell;
        if (uiShell is null)
        {
            return;
        }

        uiShell.UpdateCommandUI(fImmediateUpdate: 1);

        var pts = new[] { new POINTS { x = (short)screen.X, y = (short)screen.Y } };

        Guid cmdSetGuid = PackageGuids.guidMacrosPackageCmdSet;
        uiShell.ShowContextMenu(
            dwCompRole: 0,
            rclsidActive: ref cmdSetGuid,
            nMenuId: menuId,
            pos: pts,
            pCmdTrgtActive: null);
    }

    private static bool TrySetMacroGroupSelection(object? dataContext)
    {
        string? header = dataContext switch
        {
            CollectionViewGroup group => group.Name?.ToString(),
            _ => dataContext?.ToString(),
        };

        MacroGroupSelectionContext.SetFromHeader(header);
        return MacroGroupSelectionContext.CurrentScope is not null;
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
