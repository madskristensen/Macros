using System;
using System.Runtime.InteropServices;
using System.Threading;
using Community.VisualStudio.Toolkit;
using Macros.Engine;
using Macros.Observers;
using Macros.Onboarding;
using Macros.Options;
using Macros.StatusBar;
using Macros.ToolWindows;
using Macros.UIContexts;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace Macros;

/// <summary>
/// VSIX package entry point. Auto-loads on shell startup (no solution required) so the recorder,
/// command observer, and trigger bus can wire themselves up before the user invokes anything.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="InitializeAsync"/> wires up — in dependency order — the macro engine
/// (<see cref="IMacroService"/>), the Macros tool window, the status-bar observer that mirrors
/// engine state, and the first-run onboarding InfoBar. The InfoBar is intentionally
/// fire-and-forget so a missing editor frame can't stall package load.
/// </para>
/// </remarks>
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration(
    productName: "Macros for Visual Studio",
    productDetails: "Record once. Repeat forever — manually or on cue.",
    productId: "1.0")]
[ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideToolWindow(typeof(MacrosToolWindow.Pane),
    Style = VsDockStyle.Tabbed,
    Window = WindowGuids.SolutionExplorer)]
[ProvideToolWindowVisibility(typeof(MacrosToolWindow.Pane),
    VSConstants.UICONTEXT.NoSolution_string)]
[ProvideOptionPage(typeof(OptionsProvider.GeneralOptionsPage), "Macros", "General",
    categoryResourceID: 0, pageNameResourceID: 0, supportsAutomation: true)]
[Guid(PackageGuids.PackageGuidString)]
public sealed class MacrosPackage : ToolkitPackage
{
    private IVsRegisterPriorityCommandTarget? _priorityCommandTarget;
    private uint _commandObserverCookie;
    private UIContextActivator? _uiContextActivator;

    /// <inheritdoc />
    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await base.InitializeAsync(cancellationToken, progress);

        // 1. Register IMacroService FIRST so consumers (status bar, commands, tool window) can
        //    resolve it via VS.GetRequiredServiceAsync<IMacroService, IMacroService>().
        //    `promote: true` proffers it to the global service container so out-of-package code
        //    (e.g. MEF parts in other assemblies) can resolve it too.
        this.AddService(
            typeof(IMacroService),
            (container, ct, type) => Task.FromResult<object>(
                new MacroService(this.JoinableTaskFactory)),
            promote: true);

        // 2. Register command handlers (scans this assembly for BaseCommand<T> subclasses with
        //    [Command] attributes and wires them to the IMenuCommandService).
        await this.RegisterCommandsAsync();

        // 3. Register the priority command target so CommandObserver sees every shell command
        //    before any focused target gets it. The observer is strictly passive — see
        //    CommandObserver.Exec for the OLECMDERR_E_NOTSUPPORTED contract.
        //    Registration must happen on the UI thread; AllowsBackgroundLoading means we may
        //    still be on the threadpool here.
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        _priorityCommandTarget = await GetServiceAsync(typeof(SVsRegisterPriorityCommandTarget)) as IVsRegisterPriorityCommandTarget;
        if (_priorityCommandTarget is not null)
        {
            var observer = new CommandObserver(JoinableTaskFactory);
            _priorityCommandTarget.RegisterPriorityCommandTarget(0u, observer, out _commandObserverCookie);
        }

        // 4. Register tool windows (scans this assembly for BaseToolWindow<T> subclasses).
        this.RegisterToolWindows();

        // 5. Wire the status bar to MacroService.StateChanged.
        await StatusBarObserver.InitializeAsync(this);

        // 6. Wire UIContext activation so the VSCT VisibilityItems (Record / Stop buttons)
        //    flip correctly whenever the macro engine state changes.
        _uiContextActivator = await UIContextActivator.InitializeAsync(this);

        // 6. Fire-and-forget the first-run onboarding InfoBar; package load must not wait for an
        //    editor frame to appear.
        _ = JoinableTaskFactory.RunAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            await OnboardingInfoBar.ShowIfFirstRunAsync();
        });
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        // Package disposal is invoked by the shell on the UI thread; the unconditional
        // assertion satisfies VSTHRD108 (must be the first statement) and is a no-op on the
        // happy path. The finalizer is suppressed by the base AsyncPackage so disposing=false
        // is not exercised in practice.
        ThreadHelper.ThrowIfNotOnUIThread();

        if (disposing)
        {
            _uiContextActivator?.Dispose();
            UnregisterCommandObserver();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Unregisters the <see cref="CommandObserver"/> priority command target if one was
    /// registered. UI-thread affinity is asserted here as well as at the <see cref="Dispose"/>
    /// entry point because VSTHRD010 inspects each method in isolation.
    /// </summary>
    private void UnregisterCommandObserver()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_priorityCommandTarget is null || _commandObserverCookie == 0u)
        {
            return;
        }

        try
        {
            _priorityCommandTarget.UnregisterPriorityCommandTarget(_commandObserverCookie);
        }
        catch
        {
            // Shutdown path: shell may already have torn down the priority target list.
            // Swallowing here is intentional — there is nothing actionable to log on dispose.
        }

        _priorityCommandTarget = null;
        _commandObserverCookie = 0u;
    }
}
