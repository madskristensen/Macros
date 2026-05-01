using System;
using System.Runtime.InteropServices;
using System.Threading;
using Community.VisualStudio.Toolkit;
using EnvDTE80;
using Macros.Engine;
using Macros.Engine.Player;
using Macros.Engine.Scripting;
using Macros.Engine.Storage;
using Macros.Commands;
using Macros.Observers;
using Macros.Onboarding;
using Macros.Options;
using Macros.Recording;
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
[ProvideOptionPage(typeof(TrustedSolutionsPage), "Macros", "Trusted Solutions",
    categoryResourceID: 0, pageNameResourceID: 0, supportsAutomation: true)]
[Guid(PackageGuids.PackageGuidString)]
public sealed class MacrosPackage : ToolkitPackage
{
    private IVsRegisterPriorityCommandTarget? _priorityCommandTarget;
    private uint _commandObserverCookie;
    private UIContextActivator? _uiContextActivator;
    private RecordingCapHandler? _recordingCapHandler;
    private readonly ScriptCompilationCache _scriptCache = new();

    // Active solution directory. Read by the repo-folder provider passed to
    // FileSystemMacroStore; updated from solution-open / solution-close events
    // (wired in m3-storage-watcher). Field is updated via Volatile.Write so the
    // background-thread reader sees a fresh value without taking a lock.
    private string? _solutionDirectory;

    /// <inheritdoc />
    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await base.InitializeAsync(cancellationToken, progress);

        // 1. Register IMacroService FIRST so consumers (status bar, commands, tool window) can
        //    resolve it via VS.GetRequiredServiceAsync<IMacroService, IMacroService>().
        //    `promote: true` proffers it to the global service container so out-of-package code
        //    (e.g. MEF parts in other assemblies) can resolve it too.
        //    Storage is wired here too: the engine writes the just-recorded source to
        //    %APPDATA%\Macros\current.csx after every Stop so Play Last survives a VS restart.
        //    The folder defaults to %APPDATA%\Macros and is overridable via
        //    MacrosOptions.GlobalMacrosFolder.
        //
        //    The same storage instance is re-exposed as IMacroStore so the M3 tool window VM
        //    can enumerate / watch the named-macro library independently of the engine. Lazy<T>
        //    guarantees a single backing FileSystemMacroStore no matter which service is
        //    resolved first (the resolves cross threads via the VS service container).
        var sharedStorage = new Lazy<IMacroStore>(
            () =>
            {
                var folder = MacrosPaths.ResolveGlobalFolder(MacrosOptions.Instance.GlobalMacrosFolder);
                // The repo-folder accessor must be cheap and non-blocking — it's invoked on
                // every named-macro call. We cache the active solution's directory in
                // _solutionDirectory and update it from solution-open / solution-close
                // events; the wiring lives in m3-storage-watcher (next todo). Until then
                // the field stays null and repo-scoped storage operations throw cleanly,
                // which is the right UX while no UI surface targets repo scope yet.
                // TODO(m3-storage-watcher): subscribe to VS.Events.SolutionEvents.OnAfterOpenSolution /
                //                            OnAfterCloseSolution to update _solutionDirectory.
                Func<string?> repoProvider = () => MacrosPaths.ResolveRepoFolder(
                    Volatile.Read(ref _solutionDirectory),
                    MacrosOptions.Instance.RepoMacrosFolderName ?? "Macros");
                return new FileSystemMacroStore(folder, repoProvider);
            },
            isThreadSafe: true);

        this.AddService(
            typeof(IMacroStore),
            (_, _, _) => Task.FromResult<object>(sharedStorage.Value),
            promote: true);

        this.AddService(
            typeof(IMacroService),
            (container, ct, type) =>
            {
                return Task.FromResult<object>(
                    new MacroService(this.JoinableTaskFactory,
                        maxStepsProvider: () => MacrosOptions.Instance.MaxRecordingSteps,
                        storage: sharedStorage.Value));
            },
            promote: true);

        // 2. Register command handlers (scans this assembly for BaseCommand<T> subclasses with
        //    [Command] attributes and wires them to the IMenuCommandService).
        await this.RegisterCommandsAsync();

        // 3. Register IMacroPlayer so the play infrastructure has a resolvable producer.
        //    ScriptCompilationCache is package-lifetime (single instance on _scriptCache) so
        //    repeated plays of the same source hit the SHA-256 LRU without re-compiling.
        //    DTE2 is acquired here, before the lazy factory is invoked, to keep the factory
        //    trivially synchronous.
        var dte = await VS.GetRequiredServiceAsync<EnvDTE.DTE, DTE2>();
        this.AddService(
            typeof(IMacroPlayer),
            (_, _, _) => Task.FromResult<object>(
                new MacroPlayer(this.JoinableTaskFactory, _scriptCache, dte)),
            promote: true);

        // 4. Register the priority command target so CommandObserver sees every shell command
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

        // 7. Subscribe to recording cap events and surface the InfoBar when the cap fires.
        _recordingCapHandler = await RecordingCapHandler.InitializeAsync(this);

        // 8. Fire-and-forget the first-run onboarding InfoBar; package load must not wait for an
        //    editor frame to appear.
        _ = JoinableTaskFactory.RunAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            await OnboardingInfoBar.ShowIfFirstRunAsync();
        });

        // 9. Prime the command-name cache once so BeforeCommand / AfterCommand trigger lookups
        //    never enumerate DTE.Commands on the hot path. Fire-and-forget is intentional —
        //    the cache degrades gracefully to per-call DTE lookup when not yet primed.
        JoinableTaskFactory.RunAsync(async () =>
        {
            await CommandNameCache.Instance.PrimeAsync(dte, JoinableTaskFactory);
        }).FileAndForget("Macros/CommandNameCache");
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
            _recordingCapHandler?.Dispose();
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
