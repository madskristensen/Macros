using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Macros.Engine.Storage;
using Macros.Engine.Triggers;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace Macros.Commands.Context;

/// <summary>
/// Tool window context-menu handler for "Manage Triggers...". Loads the selected macro,
/// shows a modal dialog that lets the user add/remove triggers via UI, and (on Save)
/// rewrites the <c>.csx</c> header in place via <see cref="TriggerHeaderRewriter.Rewrite"/>.
/// </summary>
/// <remarks>
/// The body of the file is preserved byte-for-byte — only the leading comment block
/// changes. Save goes through <see cref="IMacroStore.SaveAsAsync"/> with
/// <c>overwrite: true</c>, which is the same atomic temp+swap path used for the
/// recorder's writes.
/// </remarks>
[Command(PackageGuids.guidMacrosPackageCmdSetString, PackageIds.cmdidMacrosCtxManageTriggers)]
internal sealed class ManageTriggersContextCommand : BaseCommand<ManageTriggersContextCommand>
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        var current = MacroSelectionContext.Current;
        if (current is null) return;

        var storage = await Package.GetServiceAsync(typeof(IMacroStore)) as IMacroStore
            ?? throw new InvalidOperationException("IMacroStore is not registered in the package container.");

        string? source;
        try
        {
            source = await storage.LoadByNameAsync(current.Name, current.Scope);
        }
        catch (Exception ex)
        {
            await VS.MessageBox.ShowErrorAsync("Manage Triggers", ex.Message);
            return;
        }

        if (source is null)
        {
            await VS.MessageBox.ShowErrorAsync(
                "Manage Triggers",
                $"Macro \"{current.Name}\" no longer exists.");
            return;
        }

        var bindings = TriggerDirectiveParser.Parse(source);
        var vm = new ManageTriggersDialogViewModel(current.Name, bindings, KnownEvents.All);
        var dlg = new ManageTriggersDialog { DataContext = vm };

        if (dlg.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var newSource = TriggerHeaderRewriter.Rewrite(source, vm.ToBindingsList());
            await storage.SaveAsAsync(current.Name, newSource, current.Scope, overwrite: true);
            await VS.StatusBar.ShowMessageAsync($"Macros: Updated triggers for \"{current.Name}\"");
        }
        catch (Exception ex)
        {
            await VS.MessageBox.ShowErrorAsync("Manage Triggers", ex.Message);
        }
    }
}
