using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Macros.Engine;
using Macros.Engine.Storage;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace Macros.Commands;

/// <summary>
/// Handles <c>Macros: Save As</c> — prompts the user for a name and scope via
/// <see cref="SaveAsDialog"/>, then persists the current macro into the named library.
/// </summary>
[Command(PackageGuids.CommandSetGuidString, PackageIds.cmdidMacrosSaveAs)]
internal sealed class SaveAsCommand : BaseCommand<SaveAsCommand>
{
    private IMacroService? _service;

    /// <inheritdoc />
    protected override async Task InitializeCompletedAsync()
    {
        _service = await VS.GetRequiredServiceAsync<IMacroService, IMacroService>();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        var storage = await VS.GetRequiredServiceAsync<IMacroStorage, IMacroStorage>();

        string? source = await ResolveSourceAsync(storage);
        if (string.IsNullOrEmpty(source))
        {
            await VS.MessageBox.ShowWarningAsync(
                "No macro to save",
                "Record a macro first (Ctrl+Shift+R), then use Save As.");
            return;
        }

        bool hasRepo = HasRepoScope(storage);

        var vm = new Macros.Engine.SaveAsDialogViewModel(source!, storage, hasRepo);
        var dlg = new SaveAsDialog { DataContext = vm };

        if (dlg.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await storage.SaveAsAsync(vm.Name, source!, vm.Scope, overwrite: vm.ExistsInTargetScope);
            await VS.StatusBar.ShowMessageAsync($"Macros: Saved \"{vm.Name}\" to {vm.Scope}");
        }
        catch (Exception ex)
        {
            await VS.MessageBox.ShowErrorAsync("Save failed", ex.Message);
        }
    }

    /// <inheritdoc />
    protected override void BeforeQueryStatus(EventArgs e)
    {
        Command.Enabled = _service?.CurrentMacroSource != null;
    }

    /// <summary>
    /// Loads the current macro source from persistent storage. Returns <see langword="null"/>
    /// when no <c>current.csx</c> exists (first run or never recorded). Extracted as a
    /// static helper so it can be exercised by unit tests without a VS host.
    /// </summary>
    internal static Task<string?> ResolveSourceAsync(IMacroStorage storage)
        => storage.LoadCurrentAsync();

    /// <summary>
    /// Returns <see langword="true"/> when the storage layer can compute a repo-scope path,
    /// indicating that a solution is currently open. Uses a pure-path computation
    /// (<see cref="IMacroStorage.GetMacroPath"/>) so no file-system I/O is performed.
    /// </summary>
    private static bool HasRepoScope(IMacroStorage storage)
    {
        try
        {
            storage.GetMacroPath("probe", MacroScope.Repo);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
