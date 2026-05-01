using System;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Macros.Engine.Storage;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace Macros.Commands.Context;

/// <summary>
/// Tool window context-menu handler for "Delete". Confirms with the user, then permanently
/// removes the selected macro file via <see cref="IMacroStore.DeleteAsync"/>.
/// </summary>
/// <remarks>
/// The core deletion logic is extracted into the static <see cref="DeleteCoreAsync"/> helper
/// so it can be exercised in unit tests without a VS host. The <see cref="VsDeleteCommandUI"/>
/// nested class adapts <c>VS.MessageBox</c>, <c>VS.StatusBar</c>, and <c>VS.Windows</c> into
/// the <see cref="IDeleteCommandUI"/> contract.
/// </remarks>
[Command(PackageGuids.guidMacrosPackageCmdSetString, PackageIds.cmdidMacrosCtxDelete)]
internal sealed class DeleteContextCommand : BaseCommand<DeleteContextCommand>
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        var current = MacroSelectionContext.Current;
        if (current is null)
        {
            return;
        }

        var storage = await Package.GetServiceAsync(typeof(IMacroStore)) as IMacroStore
            ?? throw new InvalidOperationException("IMacroStore is not registered in the package container.");
        await DeleteCoreAsync(current, storage, VsDeleteCommandUI.Instance);
    }

    /// <summary>
    /// Encapsulates the full deletion flow independently of the VS host surface.
    /// </summary>
    /// <param name="desc">The macro to delete.</param>
    /// <param name="storage">Storage back-end; raises <c>LibraryChanged(Removed)</c> on success.</param>
    /// <param name="ui">UI surface — real VS shell in production, test double in unit tests.</param>
    /// <param name="ct">Cancellation token forwarded to storage I/O.</param>
    internal static async Task DeleteCoreAsync(
        MacroEntry desc,
        IMacroStore storage,
        IDeleteCommandUI ui,
        CancellationToken ct = default)
    {
        bool confirmed = await ui.ConfirmDeleteAsync(desc.Name);
        if (!confirmed)
        {
            return;
        }

        bool deleted;
        try
        {
            deleted = await storage.DeleteAsync(desc.Name, desc.Scope, ct);
        }
        catch (Exception ex)
        {
            await ui.ShowErrorAsync(desc.Name, ex);
            return;
        }

        if (!deleted)
        {
            await ui.ShowAlreadyDeletedAsync(desc.Name);
            return;
        }

        await ui.ShowSuccessAsync(desc.Name);
    }

    /// <summary>
    /// Production implementation of <see cref="IDeleteCommandUI"/> that delegates to the
    /// VS Toolkit surfaces. Kept private to <see cref="DeleteContextCommand"/>; tests inject
    /// a Moq mock instead.
    /// </summary>
    private sealed class VsDeleteCommandUI : IDeleteCommandUI
    {
        public static readonly VsDeleteCommandUI Instance = new();
        private VsDeleteCommandUI() { }

        public Task<bool> ConfirmDeleteAsync(string macroName) =>
            VS.MessageBox.ShowConfirmAsync(
                "Delete Macro",
                $"Delete macro \"{macroName}\"? This cannot be undone.");

        public Task ShowAlreadyDeletedAsync(string macroName) =>
            VS.MessageBox.ShowAsync(
                "Delete Macro",
                $"Macro \"{macroName}\" was already deleted.");

        public Task ShowSuccessAsync(string macroName) =>
            VS.StatusBar.ShowMessageAsync($"Macros: Deleted \"{macroName}\".");

        public async Task ShowErrorAsync(string macroName, Exception ex)
        {
            await VS.MessageBox.ShowErrorAsync("Delete Macro", ex.Message);
            try
            {
                OutputWindowPane pane = await VS.Windows.CreateOutputWindowPaneAsync(
                    "Macros", lazyCreate: false);
                await pane.WriteLineAsync($"[Delete \"{macroName}\"] {ex}");
            }
            catch
            {
                // The output-pane write is best-effort; never mask the original error.
            }
        }
    }
}
