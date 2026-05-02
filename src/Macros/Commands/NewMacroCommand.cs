using System;
using System.IO;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Macros.Commands.Context;
using Macros.Engine.Codegen;
using Macros.Engine.Storage;
using Macros.Lifecycle;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace Macros.Commands;

[Command(PackageGuids.guidMacrosPackageCmdSetString, PackageIds.cmdidMacrosNewMacro)]
internal sealed class NewMacroCommand : BaseCommand<NewMacroCommand>
{
    protected override void BeforeQueryStatus(EventArgs e)
    {
        bool visible = CanCreateInSelectedScope();
        Command.Visible = visible;
        Command.Supported = visible;
        Command.Enabled = visible;
    }

    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        MacroScope? scope = MacroGroupSelectionContext.CurrentScope;
        if (scope is null)
        {
            return;
        }

        if (scope == MacroScope.Repo && SolutionContextTracker.Current?.HasSolution != true)
        {
            await VS.MessageBox.ShowErrorAsync("New Macro", "Cannot create a repo macro: no solution is open.");
            return;
        }

        var storage = await Package.GetServiceAsync(typeof(IMacroStore)) as IMacroStore
            ?? throw new InvalidOperationException("IMacroStore is not registered in the package container.");

        try
        {
            var created = await CreateMacroAsync(storage, scope.Value);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            await VS.Documents.OpenAsync(created.Path);
            await VS.StatusBar.ShowMessageAsync($"Macros: Created \"{created.Name}\" in {scope.Value}");
        }
        catch (Exception ex)
        {
            await VS.MessageBox.ShowErrorAsync("New Macro", ex.Message);
        }
    }

    private static bool CanCreateInSelectedScope()
    {
        MacroScope? scope = MacroGroupSelectionContext.CurrentScope;
        return scope switch
        {
            MacroScope.Global => true,
            MacroScope.Repo => SolutionContextTracker.Current?.HasSolution == true,
            _ => false,
        };
    }

    private static Task<(string Name, string Path)> CreateMacroAsync(IMacroStore storage, MacroScope scope)
        => Task.Run(async () =>
        {
            for (int suffix = 1; ; suffix++)
            {
                string name = suffix == 1 ? "New Macro" : $"New Macro {suffix}";
                string source = CSharpCodeGenerator.GenerateEmpty(name, DateTime.UtcNow);

                try
                {
                    await storage.SaveAsAsync(name, source, scope, overwrite: false).ConfigureAwait(false);
                    return (name, storage.GetMacroPath(name, scope));
                }
                catch (InvalidOperationException ex) when (ex.Message.StartsWith("Macro already exists:", StringComparison.Ordinal))
                {
                }
            }
        });
}
