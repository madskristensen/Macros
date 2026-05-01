using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.VisualStudio.Shell;

namespace Macros.Options;

[ComVisible(true)]
[Guid("6B3E4A12-9C7F-4D2B-A851-3F5C8D0E6A94")]
public class TrustedSolutionsPage : UIElementDialogPage
{
    private TrustedSolutionsPageControl? _control;

    protected override UIElement Child
    {
        get
        {
            if (_control == null)
            {
                _control = new TrustedSolutionsPageControl();
                _control.LoadFrom(MacrosOptions.Instance);
            }
            return _control;
        }
    }

    public override void SaveSettingsToStorage()
    {
        _control?.SaveTo(MacrosOptions.Instance);
        MacrosOptions.Instance.Save();
        base.SaveSettingsToStorage();
    }
}
