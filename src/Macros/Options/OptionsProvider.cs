using Community.VisualStudio.Toolkit;
using System.Runtime.InteropServices;

namespace Macros.Options;

internal sealed class OptionsProvider
{
    [ComVisible(true)]
    public sealed class GeneralOptionsPage : BaseOptionPage<MacrosOptions> { }
}
