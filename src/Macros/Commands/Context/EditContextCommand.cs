using Community.VisualStudio.Toolkit;

using Microsoft.VisualStudio.Shell;

using Task = System.Threading.Tasks.Task;

namespace Macros.Commands.Context;

/// <summary>
/// Tool window context-menu handler for "Edit Macro". Opens the selected macro's .csx file
/// in the standard VS editor with C# colorization.
/// </summary>
/// <remarks>
/// IntelliSense fidelity: VS opens .csx files with the C# language service, providing
/// syntax highlighting and basic completion. Full script-host context (e.g. #r references,
/// script globals) may not resolve perfectly — this is an acceptable limitation for M3.
/// </remarks>
[Command(PackageGuids.guidMacrosPackageCmdSetString, PackageIds.cmdidMacrosCtxEdit)]
internal sealed class EditContextCommand : BaseCommand<EditContextCommand>
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        var current = MacroSelectionContext.Current;
        if (current is null)
        {
            return;
        }

        var (ok, path, errorMessage) = EditMacroResolver.Resolve(current, System.IO.File.Exists);
        if (!ok)
        {
            await VS.MessageBox.ShowErrorAsync("Edit Macro", errorMessage);
            return;
        }

        await VS.Documents.OpenAsync(path);
        await VS.StatusBar.ShowMessageAsync($"Macros: Editing \"{current.Name}\"");
    }
}
