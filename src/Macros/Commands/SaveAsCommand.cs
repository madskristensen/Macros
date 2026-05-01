using System;
using System.IO;
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
[Command(PackageGuids.guidMacrosPackageCmdSetString, PackageIds.cmdidMacrosSaveAs)]
internal sealed class SaveAsCommand : BaseCommand<SaveAsCommand>
{
    private IMacroService? _service;
    private bool _resolveStarted;

    /// <summary>
    /// Lazily resolves <see cref="IMacroService"/> on first use and caches the result.
    /// Uses the package container directly instead of <c>ServiceProvider.GlobalProvider</c>,
    /// which does not reliably surface promoted custom services after <c>SetSite</c> returns.
    /// </summary>
    private async Task<IMacroService> GetServiceAsync()
    {
        if (_service is not null) return _service;
        return _service = await Package.GetServiceAsync(typeof(IMacroService)) as IMacroService
            ?? throw new InvalidOperationException(
                "IMacroService is not registered in the package container.");
    }

    private void EnsureResolveStarted()
    {
        if (_service is not null || _resolveStarted) return;
        _resolveStarted = true;
        Package.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await GetServiceAsync();
            }
            catch (Exception ex)
            {
                _resolveStarted = false;
                await ex.LogAsync();
            }
        }).FileAndForget("Macros/SaveAsCommand/ServiceResolve");
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        // Resolve IMacroService here too so BeforeQueryStatus has a cached value once the user
        // has invoked the command at least once. (IMacroStore was already lazy-resolved per-call.)
        try
        {
            await GetServiceAsync();
        }
        catch (Exception ex)
        {
            await ex.LogAsync();
            // IMacroStore failure below is more likely to be the real blocker; continue so we
            // surface a useful error rather than swallowing the click.
        }

        var storage = await Package.GetServiceAsync(typeof(IMacroStore)) as IMacroStore
            ?? throw new InvalidOperationException("IMacroStore is not registered in the package container.");

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
        catch (OperationCanceledException)
        {
            // User cancelled (rare here — no token wired to the dialog) — treat as silent.
        }
        catch (Exception ex)
        {
            var (title, message) = MapSaveError(ex, vm.Name, vm.Scope);
            await VS.MessageBox.ShowErrorAsync(title, message);
        }
    }

    /// <inheritdoc />
    protected override void BeforeQueryStatus(EventArgs e)
    {
        EnsureResolveStarted();
        Command.Enabled = _service?.CurrentMacroSource != null;
    }

    /// <summary>
    /// Loads the current macro source from persistent storage. Returns <see langword="null"/>
    /// when no <c>current.csx</c> exists (first run or never recorded). Extracted as a
    /// static helper so it can be exercised by unit tests without a VS host.
    /// </summary>
    internal static Task<string?> ResolveSourceAsync(IMacroStore storage)
        => storage.LoadCurrentAsync();

    /// <summary>
    /// Maps a save-failure <see cref="Exception"/> to a user-facing (title, message) pair.
    /// Pulled out as a static helper so the mapping table can be unit-tested without
    /// going through a VS host. The intent is to replace raw OS / .NET messages
    /// (<c>"Access to the path 'X:\…' is denied."</c>) with action-oriented guidance
    /// (<c>"Check folder permissions or pick a different scope."</c>).
    /// </summary>
    /// <param name="ex">The exception thrown by <see cref="IMacroStore.SaveAsAsync"/>.</param>
    /// <param name="name">Macro name being saved (used in the message).</param>
    /// <param name="scope">Target scope (used in the message).</param>
    internal static (string Title, string Message) MapSaveError(Exception ex, string name, MacroScope scope)
    {
        switch (ex)
        {
            case UnauthorizedAccessException:
                return (
                    "Save failed — access denied",
                    $"Couldn't write \"{name}\" to {scope}. The macros folder is read-only or " +
                    "your account lacks write permission. " +
                    (scope == MacroScope.Repo
                        ? "Try saving to Global instead, or check the .vs\\Macros folder permissions."
                        : "Pick a writable folder under Tools → Options → Macros → Global macros folder."));

            case DirectoryNotFoundException:
                return (
                    "Save failed — folder not found",
                    $"Couldn't write \"{name}\" to {scope}. The target folder does not exist " +
                    "and could not be created. Verify the path under Tools → Options → Macros.");

            case PathTooLongException:
                return (
                    "Save failed — path too long",
                    $"The full path for \"{name}\" exceeds the operating-system limit. " +
                    "Use a shorter name or move the macros folder higher up in the file system.");

            case IOException io:
                // Catches disk full, sharing-violation (other VS instance), etc.
                return (
                    "Save failed — I/O error",
                    $"Couldn't write \"{name}\" to {scope}: {io.Message}");

            case InvalidOperationException:
                // Storage layer's own contract violations — message is already user-facing
                // ("Macro already exists: …", "No repo scope available — solution is not loaded.").
                return ("Save failed", ex.Message);

            default:
                return ("Save failed", ex.Message);
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when the storage layer can compute a repo-scope path,
    /// indicating that a solution is currently open. Uses a pure-path computation
    /// (<see cref="IMacroStore.GetMacroPath"/>) so no file-system I/O is performed.
    /// </summary>
    private static bool HasRepoScope(IMacroStore storage)
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
