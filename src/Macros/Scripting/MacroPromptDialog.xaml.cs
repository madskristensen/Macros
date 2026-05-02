using System;
using Microsoft.VisualStudio.PlatformUI;

namespace Macros.Scripting;

internal partial class MacroPromptDialog : DialogWindow
{
    public MacroPromptDialog(string label, string defaultValue)
    {
        if (label is null) throw new ArgumentNullException(nameof(label));
        if (defaultValue is null) throw new ArgumentNullException(nameof(defaultValue));

        InitializeComponent();
        PromptLabel.Text = label;
        InputTextBox.Text = defaultValue;
        Loaded += (_, _) =>
        {
            InputTextBox.Focus();
            InputTextBox.SelectAll();
        };
    }

    public string InputText => InputTextBox.Text ?? string.Empty;

    private void OkButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
