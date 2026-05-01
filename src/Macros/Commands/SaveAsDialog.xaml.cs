using Microsoft.VisualStudio.PlatformUI;

namespace Macros.Commands;

/// <summary>
/// Code-behind for the <c>Save Macro As</c> modal dialog. DataContext is set by the
/// caller (<see cref="SaveAsCommand"/>) before <see cref="ShowDialog"/> is invoked.
/// </summary>
internal partial class SaveAsDialog : DialogWindow
{
    public SaveAsDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => NameTextBox.Focus();
    }

    private void SaveButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is Macros.Engine.SaveAsDialogViewModel vm && vm.CanSave)
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
