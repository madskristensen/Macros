using System;
using System.Runtime.InteropServices;
using System.Threading;
using Community.VisualStudio.Toolkit;
using EnvDTE;
using EnvDTE80;
using Macros.Engine;
using Macros.Engine.Player;
using Macros.Engine.Scripting;
using Macros.Engine.Storage;
using Macros.Engine.Triggers;
using Macros.Commands;
using Macros.Lifecycle;
using Macros.Observers;
using Macros.Onboarding;
using Macros.Options;
using Macros.Recording;
using Macros.StatusBar;
using Macros.ToolWindows;
using Macros.Triggers;
using Macros.Trust;
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
    private MacroTriggerRegistry? _triggerRegistry;
    private CommandTriggerDispatcher? _commandTriggerDispatcher;
    private MacroEventBus? _eventBus;
    // CommandEvents is a COM object the shell only weak-references through our subscription;
    // GC'ing the wrapper detaches the event handlers and silently breaks AfterCommand
    // dispatch. Store it on the package field so it lives as long as we do.
    private EnvDTE.CommandEvents? _commandEvents;
    private _dispCommandEvents_AfterExecuteEventHandler? _afterExecuteHandler;
    private readonly ScriptCompilationCache _scriptCache = new();

    // Solution open / close listener. Owned by the package so its toolkit event
    // subscriptions stay rooted; disposed on package shutdown. Wires the active
    // solution directory into the repo-folder provider passed to RepoMacroStore,
    // closing the M3 deferred TODO that read
    // "TODO(m3-storage-watcher or m3-tool-window): wire solution events".
    private SolutionContextTracker? _solutionTracker;

    // Scope-restricted stores fronted by the M4 CompositeMacroStore. The composite owns
    // both children for disposal (ownsChildren: true) — keeping references here lets the
    // dispose path tear them down deterministically and lets test hooks observe them.
    private GlobalMacroStore? _globalMacroStore;
    private RepoMacroStore? _repoMacroStore;
    private CompositeMacroStore? _compositeMacroStore;

    // M4 trust gate: surfaces an InfoBar on solution open whenever the active solution
    // contains repo macros with auto-run triggers and the user has neither trusted nor
    // blocked it yet. Owned on a field so the SolutionChanged subscription stays rooted
    // and is unwired deterministically on package dispose.
    private TrustGateInfoBar? _trustGateInfoBar;

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
        //    guarantees a single backing store no matter which service is resolved first
        //    (the resolves cross threads via the VS service container).
        //
        //    M4: the storage is now a CompositeMacroStore routing between a GlobalMacroStore
        //    (rooted at MacrosPaths.ResolveGlobalFolder) and a RepoMacroStore (rooted at the
        //    active solution's .vs\Macros folder via _solutionTracker). The composite owns
        //    both halves for disposal; the field references survive on the package so tests
        //    and the dispose path can observe / tear them down deterministically.
        var sharedStorage = new Lazy<IMacroStore>(
            () =>
            {
                var folder = MacrosPaths.ResolveGlobalFolder(MacrosOptions.Instance.GlobalMacrosFolder);
                // The repo-folder accessor must be cheap and non-blocking — it's invoked on
                // every named-macro call. SolutionContextTracker (constructed below) caches
                // the active solution's directory and updates it from solution-open /
                // solution-close events. When no solution is open the provider returns
                // null and repo-scoped storage operations throw cleanly; the composite's
                // ListAllAsync silently degrades to global-only in that case so the tool
                // window can render a no-solution startup without special-casing.
                var global = new GlobalMacroStore(folder);
                var repo = new RepoMacroStore(() => _solutionTracker?.GetCurrentRepoMacrosFolder());
                _globalMacroStore = global;
                _repoMacroStore = repo;
                var composite = new CompositeMacroStore(global, repo, ownsChildren: true);
                _compositeMacroStore = composite;
                return composite;
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

        // Register IMacroEventBus with the kill-switch provider so VS event triggers
        // honour DisableAllTriggers without requiring a registry rebuild.
        var reentranceGuard = new TriggerReentranceGuard();
        _eventBus = new MacroEventBus(isDisabledProvider: () => MacrosOptions.Instance.DisableAllTriggers, guard: reentranceGuard);
        this.AddService(
            typeof(IMacroEventBus),
            (_, _, _) => Task.FromResult<object>(_eventBus),
            promote: true);

        // 2. Register command handlers (scans this assembly for BaseCommand<T> subclasses with
        //    [Command] attributes and wires them to the IMenuCommandService).
        await this.RegisterCommandsAsync();

        // 2a. Initialize the solution tracker BEFORE any consumer touches the shared storage
        //     (the trigger registry below resolves sharedStorage.Value, which lazily builds
        //     the CompositeMacroStore and reads _solutionTracker to wire the repo half).
        //     The tracker subscribes to VS.Events.SolutionEvents.OnAfterOpenSolution /
        //     OnAfterCloseSolution and is read on every named-macro call by the repo-folder
        //     provider above — this closes the M3 deferred TODO that previously left
        //     _solutionDirectory at null forever.
        _solutionTracker = await SolutionContextTracker.InitializeAsync(this);

        // 2b. Wire the M4 trust-gate InfoBar. Subscribes to SolutionChanged on the tracker
        //     above and shows an InfoBar at the top of the editor whenever a solution opens
        //     that carries repo macros with auto-run triggers and is neither trusted nor
        //     blocked. Triggers stay dormant until the user makes a choice.
        _trustGateInfoBar = await TrustGateInfoBar.InitializeAsync(this, _solutionTracker);

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
        //    before any focused target gets it. The observer normally returns
        //    OLECMDERR_E_NOTSUPPORTED so the chain continues; the only exception is when a
        //    BeforeCommand macro vetoes the command via Trigger.CancelCommand().
        //    Registration must happen on the UI thread; AllowsBackgroundLoading means we may
        //    still be on the threadpool here.
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        // Construct the trigger registry over the shared store so the dispatcher can probe
        // O(1) per command. The kill-switch hook funnels MacrosOptions.DisableAllTriggers
        // back into the registry without taking a project reference on the options DLL.
        _triggerRegistry = new MacroTriggerRegistry(
            sharedStorage.Value,
            JoinableTaskFactory,
            disableAllTriggersProvider: () => MacrosOptions.Instance.DisableAllTriggers);

        // Construct the dispatcher. The MacroPlayer here is a fresh instance — we cannot
        // resolve via VS.GetRequiredServiceAsync from inside the package's own
        // InitializeAsync without risking a deadlock against the service container that
        // hasn't finished registering us yet. The script cache and DTE are shared with the
        // proffered IMacroPlayer service so cache hits are still cross-instance.
        var dispatcherPlayer = new MacroPlayer(JoinableTaskFactory, _scriptCache, dte);
        _commandTriggerDispatcher = new CommandTriggerDispatcher(
            _triggerRegistry,
            dispatcherPlayer,
            CommandNameCache.Instance,
            beforeTimeoutMsProvider: () => MacrosOptions.Instance.BeforeCommandTimeoutMs,
            JoinableTaskFactory,
            tracker: null /* m4-auto-disable wires this */,
            guard: reentranceGuard);

        _priorityCommandTarget = await GetServiceAsync(typeof(SVsRegisterPriorityCommandTarget)) as IVsRegisterPriorityCommandTarget;
        if (_priorityCommandTarget is not null)
        {
            var observer = new CommandObserver(JoinableTaskFactory, _commandTriggerDispatcher);
            _priorityCommandTarget.RegisterPriorityCommandTarget(0u, observer, out _commandObserverCookie);
        }

        // Subscribe to DTE.Events.CommandEvents.AfterExecute so AfterCommand triggers fire
        // once the real command handler has completed. The priority command target's Exec
        // runs strictly BEFORE the handler and has no completion callback, so we can't
        // dispatch AfterCommand from the same code path. The CommandEvents wrapper must be
        // stored on a field — letting it GC silently detaches the subscription.
        _commandEvents = dte.Events.CommandEvents;
        _afterExecuteHandler = (string guid, int id, object input, object output) =>
        {
            try
            {
                if (Guid.TryParse(guid, out var g))
                {
                    _commandTriggerDispatcher?.DispatchAfter(g, (uint)id);
                }
            }
            catch
            {
                // AfterExecute fires from the shell; a throw here would tear down the COM
                // event source and silently disable AfterCommand for the rest of the session.
            }
        };
        _commandEvents.AfterExecute += _afterExecuteHandler;

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
            UnsubscribeCommandEvents();
            _triggerRegistry?.Dispose();
            _eventBus?.Dispose();
            _trustGateInfoBar?.Dispose();
            _solutionTracker?.Dispose();
            // Composite owns both children — disposing it tears down the global + repo
            // FileSystemMacroStore halves (and their watchers / semaphores) in one shot.
            _compositeMacroStore?.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Test / future-composite hook: the M4 GlobalMacroStore wrapped by the
    /// <see cref="CompositeMacroStore"/>. Returns <see langword="null"/> until the lazy
    /// shared storage has been realized (i.e. a consumer has resolved
    /// <see cref="IMacroStore"/> or the trigger registry has been built).
    /// </summary>
    internal GlobalMacroStore? GlobalMacroStore => _globalMacroStore;

    /// <summary>
    /// Test / future-composite hook: the M4 RepoMacroStore wrapped by the
    /// <see cref="CompositeMacroStore"/>. The store's repo-folder provider is wired through
    /// <see cref="SolutionTracker"/> so it tracks the active solution in real time. Returns
    /// <see langword="null"/> until the lazy shared storage has been realized.
    /// </summary>
    internal RepoMacroStore? RepoMacroStore => _repoMacroStore;

    /// <summary>
    /// Test / future-composite hook: the M4 <see cref="Macros.Engine.Storage.CompositeMacroStore"/>
    /// that fronts both scope-aware halves and is registered as <see cref="IMacroStore"/>.
    /// Returns <see langword="null"/> until the lazy shared storage has been realized.
    /// </summary>
    internal CompositeMacroStore? CompositeMacroStore => _compositeMacroStore;

    /// <summary>
    /// Test / future-composite hook: the solution-lifecycle tracker. Returns
    /// <see langword="null"/> until <see cref="InitializeAsync"/> has run.
    /// </summary>
    internal SolutionContextTracker? SolutionTracker => _solutionTracker;

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

    /// <summary>
    /// Detaches the <c>CommandEvents.AfterExecute</c> subscription added during
    /// <see cref="InitializeAsync"/>. Defensive against a partially-initialised package
    /// (subscription field is <see langword="null"/> when the DTE wiring failed early).
    /// </summary>
    private void UnsubscribeCommandEvents()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_commandEvents is not null && _afterExecuteHandler is not null)
        {
            try
            {
                _commandEvents.AfterExecute -= _afterExecuteHandler;
            }
            catch
            {
                // Shutdown path: DTE may already have been torn down by the shell.
            }
        }

        _commandEvents = null;
        _afterExecuteHandler = null;
    }
}
