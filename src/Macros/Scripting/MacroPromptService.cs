using System.Threading.Tasks;
using Macros.Engine.Scripting;
using Microsoft.VisualStudio.Shell;

namespace Macros.Scripting;

internal sealed class MacroPromptService : IMacroPromptService
{
    public async Task<string> PromptAsync(string label, string defaultValue)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var dialog = new MacroPromptDialog(label, defaultValue);
        return dialog.ShowDialog() == true ? dialog.InputText : defaultValue;
    }
}
