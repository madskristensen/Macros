using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Community.VisualStudio.Toolkit;
using Macros.Commands.Context;
using Macros.Engine.Storage;
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
    private const string MacroDragDataFormat = "Macros.ToolWindows.MacroDragData";
    private const string SampleDragDataFormat = "Macros.ToolWindows.SampleDragData";
    private Point? _dragStartPoint;
    private object? _dragData;
    private bool _dragInProgress;

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

    private void Row_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject origin && FindAncestor<ButtonBase>(origin) is not null)
        {
            ClearDragState();
            return;
        }

        if (sender is FrameworkElement fe && (fe.DataContext is MacroItemViewModel || fe.DataContext is SampleTemplateItemViewModel))
        {
            _dragStartPoint = e.GetPosition(this);
            _dragData = fe.DataContext;
            return;
        }

        ClearDragState();
    }

    private void MacroRow_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MacroItemViewModel item && item.IsSample)
        {
            TryStartDrag(sender, e, SampleDragDataFormat, DragDropEffects.Copy);
            return;
        }

        TryStartDrag(sender, e, MacroDragDataFormat, DragDropEffects.Move);
    }

#pragma warning disable VSTHRD100 // WPF drag/drop handlers are event-based; exceptions are handled by the VM methods.
    private async void MacroGroups_Drop(object sender, DragEventArgs e)
#pragma warning restore VSTHRD100
    {
        if (DataContext is not MacrosToolWindowViewModel viewModel)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        DragDropEffects effect = ResolveDropEffect(e, out var targetScope, out var payload);
        if (effect == DragDropEffects.None || targetScope is null)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        bool completed = payload switch
        {
            MacroItemViewModel macro when macro.IsSample => await viewModel.CopySampleToScopeAsync(macro, targetScope.Value),
            MacroItemViewModel macro => await viewModel.MoveMacroAsync(macro, targetScope.Value),
            SampleTemplateItemViewModel sample => await viewModel.CopySampleToScopeAsync(sample, targetScope.Value),
            _ => false,
        };

        e.Effects = completed ? effect : DragDropEffects.None;
        e.Handled = true;
    }

    private void MacroGroups_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = ResolveDropEffect(e, out _, out _);
        e.Handled = true;
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

        if (item.IsSample && item.SampleTemplate is not null)
        {
            if (DataContext is MacrosToolWindowViewModel viewModel)
            {
                _ = viewModel.OpenSampleByTemplateAsync(item.SampleTemplate);
            }

            e.Handled = true;
            return;
        }

        if (item.PlayCommand.CanExecute(null))
        {
            item.PlayCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void MacroRow_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (sender is FrameworkElement fe && fe.DataContext is MacroItemViewModel item)
        {
            if (item.IsSample)
            {
                return;
            }

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
            if (item.IsSample)
            {
                if (DataContext is MacrosToolWindowViewModel viewModel && item.SampleTemplate is not null)
                {
                    _ = viewModel.OpenSampleByTemplateAsync(item.SampleTemplate);
                }
            }
            else
            {
                MacroSelectionContext.Current = item.Descriptor;
                ExecuteContextCommand(PackageIds.cmdidMacrosCtxPlay);
            }

            e.Handled = true;
            return;
        }

        if (item.IsSample)
        {
            e.Handled = isF7 || isF2 || isContextMenu;
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

    private void TryStartDrag(object sender, MouseEventArgs e, string format, DragDropEffects allowedEffects)
    {
        if (_dragInProgress || _dragStartPoint is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (sender is not FrameworkElement fe || !ReferenceEquals(_dragData, fe.DataContext))
        {
            return;
        }

        Point currentPosition = e.GetPosition(this);
        if (Math.Abs(currentPosition.X - _dragStartPoint.Value.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(currentPosition.Y - _dragStartPoint.Value.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragInProgress = true;
        try
        {
            var dataObject = new DataObject();
            dataObject.SetData(format, fe.DataContext);
            DragDrop.DoDragDrop(fe, dataObject, allowedEffects);
        }
        finally
        {
            _dragInProgress = false;
            ClearDragState();
        }
    }

    private DragDropEffects ResolveDropEffect(DragEventArgs e, out MacroScope? targetScope, out object? payload)
    {
        payload = null;
        if (!TryResolveDropTargetScope(e.OriginalSource as DependencyObject, out MacroScope scope))
        {
            targetScope = null;
            return DragDropEffects.None;
        }

        targetScope = scope;

        if (e.Data.GetDataPresent(MacroDragDataFormat) && e.Data.GetData(MacroDragDataFormat) is MacroItemViewModel macro)
        {
            payload = macro;
            if (macro.IsSample)
            {
                return macro.SampleTemplate is null ? DragDropEffects.None : DragDropEffects.Copy;
            }

            return macro.Scope == scope ? DragDropEffects.None : DragDropEffects.Move;
        }

        if (e.Data.GetDataPresent(SampleDragDataFormat) && e.Data.GetData(SampleDragDataFormat) is SampleTemplateItemViewModel sample)
        {
            payload = sample;
            return DragDropEffects.Copy;
        }

        return DragDropEffects.None;
    }

    private static bool TryResolveDropTargetScope(DependencyObject? origin, out MacroScope scope)
    {
        DependencyObject? current = origin;
        while (current is not null)
        {
            if (current is FrameworkElement fe && TryMapDropScope(fe.DataContext, out scope))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        scope = default;
        return false;
    }

    private static bool TryMapDropScope(object? dataContext, out MacroScope scope)
    {
        switch (dataContext)
        {
            case MacroItemViewModel macro when macro.IsSample:
                scope = default;
                return false;
            case MacroItemViewModel macro:
                scope = macro.Scope;
                return true;
            case CollectionViewGroup group:
                return TryMapDropScope(group.Name?.ToString(), out scope);
            case string header when header == "Samples":
                scope = default;
                return false;
            case string header when header == "Repo":
                scope = MacroScope.Repo;
                return true;
            case string header when header == "Global" || header == "Shadowed Global Macros":
                scope = MacroScope.Global;
                return true;
            default:
                scope = default;
                return false;
        }
    }

#pragma warning disable VSTHRD100 // WPF click handlers are event-based; exceptions are handled by the VM methods.
    private async void PrimaryActionButton_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not MacroItemViewModel item)
        {
            return;
        }

        if (item.IsSample)
        {
            if (DataContext is MacrosToolWindowViewModel viewModel && item.SampleTemplate is not null)
            {
                await viewModel.OpenSampleByTemplateAsync(item.SampleTemplate);
                e.Handled = true;
            }

            return;
        }

        if (item.PlayCommand.CanExecute(null))
        {
            item.PlayCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void ClearDragState()
    {
        _dragStartPoint = null;
        _dragData = null;
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
