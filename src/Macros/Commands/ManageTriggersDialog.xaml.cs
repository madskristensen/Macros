using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.VisualStudio.PlatformUI;

namespace Macros.Commands;

/// <summary>
/// Code-behind for the M4 <c>Manage Triggers</c> modal dialog. DataContext is set by the
/// caller (<see cref="Context.ManageTriggersContextCommand"/>) before
/// <see cref="System.Windows.Window.ShowDialog"/> is invoked.
/// </summary>
internal partial class ManageTriggersDialog : DialogWindow
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    public ManageTriggersDialog()
    {
        InitializeComponent();
        // Initial focus on the Kind combo so keyboard users land on the first input
        // rather than the Save/Cancel buttons rendered later in tab order.
        Loaded += (_, _) => KindCombo.Focus();
        SourceInitialized += (_, _) => ApplyTitleBarTheme();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

    private void ApplyTitleBarTheme()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        if (TryGetResourceColor(EnvironmentColors.ToolWindowBackgroundBrushKey, out var captionColor))
        {
            int captionColorRef = ToColorRef(captionColor);
            _ = DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref captionColorRef, sizeof(int));
        }

        if (TryGetResourceColor(EnvironmentColors.ToolWindowTextBrushKey, out var textColor))
        {
            int textColorRef = ToColorRef(textColor);
            _ = DwmSetWindowAttribute(handle, DwmwaTextColor, ref textColorRef, sizeof(int));
        }

        var darkMode = 1;
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));
    }

    private bool TryGetResourceColor(object key, out System.Windows.Media.Color color)
    {
        if (TryFindResource(key) is System.Windows.Media.SolidColorBrush brush)
        {
            color = brush.Color;
            return true;
        }

        color = default;
        return false;
    }

    private static int ToColorRef(System.Windows.Media.Color color)
        => color.R | (color.G << 8) | (color.B << 16);

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ManageTriggersDialogViewModel vm)
        {
            vm.AddCurrentInputAsBinding();
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ManageTriggersDialogViewModel vm)
        {
            var hasPendingInput =
                !string.IsNullOrWhiteSpace(vm.NewTriggerName)
                || !string.IsNullOrWhiteSpace(vm.NewTriggerFilters);

            // If the user has typed a new trigger but forgot to click Add, promote it to the
            // list on Save. If the pending input is invalid, keep the dialog open so the
            // existing validation message can guide the correction.
            if (hasPendingInput)
            {
                if (!vm.CanAddTrigger)
                {
                    return;
                }

                vm.AddCurrentInputAsBinding();
            }
        }

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
