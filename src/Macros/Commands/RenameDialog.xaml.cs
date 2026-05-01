using Microsoft.VisualStudio.PlatformUI;

namespace Macros.Commands;

/// <summary>
/// Code-behind for the <c>Rename Macro</c> modal dialog. DataContext is set by the
/// caller (<see cref="Context.RenameContextCommand"/>) before <see cref="ShowDialog"/> is invoked.
/// </summary>
internal partial class RenameDialog : DialogWindow
{
    public RenameDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => NewNameTextBox.Focus();
    }

    private void RenameButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is RenameDialogViewModel vm && vm.CanRename)
        {
            DialogResult = true;
            Close();
        }
    }

    private void CancelButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
