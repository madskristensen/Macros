using System.Windows;
using Microsoft.VisualStudio.PlatformUI;

namespace Macros.Commands;

/// <summary>
/// Code-behind for the M4 <c>Manage Triggers</c> modal dialog. DataContext is set by the
/// caller (<see cref="Context.ManageTriggersContextCommand"/>) before
/// <see cref="System.Windows.Window.ShowDialog"/> is invoked.
/// </summary>
internal partial class ManageTriggersDialog : DialogWindow
{
    public ManageTriggersDialog()
    {
        InitializeComponent();
        // Initial focus on the Kind combo so keyboard users land on the first input
        // rather than the Save/Cancel buttons rendered later in tab order.
        Loaded += (_, _) => KindCombo.Focus();
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ManageTriggersDialogViewModel vm)
        {
            vm.AddCurrentInputAsBinding();
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
