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
using Macros.Commands.Context;
using Macros.Lifecycle;
using Macros.Observers;
using Macros.Onboarding;
using Macros.Options;
using Macros.Recording;
using Macros.StatusBar;
using Macros.Scripting;
using Macros.Skills;
using Macros.ToolWindows;
using Macros.Triggers;
using Macros.Trust;
using Macros.UIContexts;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using System.Threading.Tasks;
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
// IsAsyncQueryable = true makes the promoted services discoverable through the global
// AsyncServiceProvider (the path Community.VisualStudio.Toolkit's VS.GetRequiredServiceAsync
// uses). Without these declarations, AddService(..., promote: true) succeeds against the
// package's own container but the service is not visible to out-of-package consumers
// resolving through the global provider — Assumes.Present then throws
// "Cannot find an instance of the ... service." at the first global lookup.
[ProvideService(typeof(IMacroService), IsAsyncQueryable = true)]
[ProvideService(typeof(IMacroStore), IsAsyncQueryable = true)]
[ProvideService(typeof(IMacroEventBus), IsAsyncQueryable = true)]
[ProvideService(typeof(IMacroPlayer), IsAsyncQueryable = true)]
[ProvideMenuResource("Menus.ctmenu", 1)]
// Registers the Macros tool window as a key-binding scope. The GUID matches
// MacrosToolWindow.Pane so VSCT key bindings declared with editor="guidMacrosToolWindow"
// (Enter -> Play, F2 -> Rename) are recognized as scoped bindings; without this attribute
// the shell does not know about the scope and therefore won't auto-render the gesture
// text on the Play / Rename context menu items. Resource ID 1000 points to the "Macros"
// scope name in VSPackage.resx (shown in Tools > Options > Environment > Keyboard).
[ProvideKeyBindingTable("a4c1b2d8-3e5f-4a6b-9c7d-8e0f1a2b3c4d", 1000)]
[ProvideToolWindow(typeof(MacrosToolWindow.Pane),
    Style = VsDockStyle.Tabbed,
    Window = WindowGuids.SolutionExplorer)]
[ProvideToolWindowVisibility(typeof(MacrosToolWindow.Pane),
    VSConstants.UICONTEXT.NoSolution_string)]
[ProvideOptionPage(typeof(OptionsProvider.GeneralOptionsPage), "Macros", "General",
    categoryResourceID: 0, pageNameResourceID: 0, supportsAutomation: true)]
[ProvideOptionPage(typeof(TrustedSolutionsPage), "Macros", "Trusted Solutions",
    categoryResourceID: 0, pageNameResourceID: 0, supportsAutomation: true)]
[Guid(PackageGuids.guidMacrosPackageString)]
public sealed class MacrosPackage : ToolkitPackage
{
    /// <summary>
    /// Process-wide handle to the loaded <see cref="MacrosPackage"/> singleton. Set as the
    /// VERY FIRST line of <see cref="InitializeAsync"/> so observers / handlers wired during
    /// package init can call <see cref="AsyncPackage.GetServiceAsync"/> directly against the
    /// package's own service container — bypassing the global <see cref="VS"/> service
    /// container which only sees promoted services AFTER SetSite completes (i.e. after
    /// InitializeAsync has fully run). Cleared in <see cref="Dispose"/>.
    /// </summary>
    public static MacrosPackage? Instance { get; private set; }

    private IVsRegisterPriorityCommandTarget? _priorityCommandTarget;
    private uint _commandObserverCookie;
    private UIContextActivator? _uiContextActivator;
    private RecordingCapHandler? _recordingCapHandler;
    private MacroTriggerRegistry? _triggerRegistry;
    private CommandTriggerDispatcher? _commandTriggerDispatcher;
    private EventTriggerDispatcher? _eventTriggerDispatcher;
    private MacroFailureTracker? _failureTracker;
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

    // Keeps the .intellisense/Macros.Intellisense.csx shim files current in both the
    // global and per-repo macro stores. Attached to _solutionTracker so the repo shim
    // is seeded / refreshed whenever a solution opens without any extra VS shell deps.
    private IntelliSenseShimRefresher? _shimRefresher;

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

    // One-shot InfoBar shown the first time a .csx macro file is opened in the editor.
    // Persists HasShownTriggerHint so the hint never reappears after dismissal.
    private TriggerHintInfoBar? _triggerHintInfoBar;

    // Direct reference to the IMacroService instance — set inside the AddService factory so
    // the document-open recording handler can push OnFileOpen(path) without a service-
    // container round-trip on the hot path. Volatile so the UI-thread handler sees the
    // write that happened on whichever thread first resolved the service.
    private volatile IMacroService? _recordingServiceRef;

    // DocumentEvents subscription used to forward file opens into the recording session.
    // Stored on a field so the event source stays rooted and the handler can be unsubscribed
    // deterministically on package dispose (same pattern as _commandEvents).
    private Community.VisualStudio.Toolkit.DocumentEvents? _recordingDocumentEvents;

    /// <inheritdoc />
    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        // Publish the package handle BEFORE awaiting anything. Observers / handlers wired
        // below resolve our custom services (IMacroService, IMacroStore, IMacroEventBus,
        // IMacroPlayer, IMacroTriggerRegistry) via this.GetServiceAsync(...) — that path
        // queries the package's own container, which is populated by AddService(...) calls
        // synchronously, instead of the global VS.GetRequiredServiceAsync<T,T>() path that
        // only sees promoted services AFTER SetSite has fully completed.
        Instance = this;

        await base.InitializeAsync(cancellationToken, progress);

        // Keep skill installation out of the critical package-load path; it completes in the background.
        SkillInstaller.InstallAsync(JoinableTaskFactory);

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
                var folder = MacrosPaths.ResolveGlobalFolderOrFallback(
                    MacrosOptions.Instance.GlobalMacrosFolder,
                    out bool usedFallback);
                if (usedFallback)
                {
                    // The user typed a malformed path in Tools → Options. Fall back to the
                    // documented default and log to the activity log so the failure is
                    // discoverable without crashing the storage layer at first save. We
                    // intentionally don't pop a modal here — package init must stay quiet.
                    System.Diagnostics.Trace.WriteLine(
                        "Macros: configured GlobalMacrosFolder is invalid; falling back to default %APPDATA%\\Macros.");
                }
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
                var svc = new MacroService(this.JoinableTaskFactory,
                    maxStepsProvider: () => MacrosOptions.Instance.MaxRecordingSteps,
                    storage: sharedStorage.Value);
                // Publish the instance so the document-open recording handler (wired below)
                // can call svc.CurrentSession.OnFileOpen without a service-container lookup.
                _recordingServiceRef = svc;
                return Task.FromResult<object>(svc);
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

        // 2. Register command handlers — explicit per-command calls so a failure in any
        //    single command produces a precise stack trace instead of silently aborting
        //    the entire reflection sweep.
        await RegisterCommandsAsync();

        // 2a. Initialize the solution tracker BEFORE any consumer touches the shared storage
        //     (the trigger registry below resolves sharedStorage.Value, which lazily builds
        //     the CompositeMacroStore and reads _solutionTracker to wire the repo half).
        //     The tracker subscribes to VS.Events.SolutionEvents.OnAfterOpenSolution /
        //     OnAfterCloseSolution and is read on every named-macro call by the repo-folder
        //     provider above — this closes the M3 deferred TODO that previously left
        //     _solutionDirectory at null forever.
        _solutionTracker = await SolutionContextTracker.InitializeAsync(this);
        SolutionContextTracker.Current = _solutionTracker;

        // 2a-bis. Seed the IntelliSense shim files in the global and per-repo macro folders.
        //         The shim is what makes `using static Helpers`, `DTE.…`, `VS.StatusBar.…` and the
        //         script globals (`DTE`, `Context`, `Trigger`) resolve in the C# editor when the
        //         user opens a .csx for editing. The runtime player ignores the shim via a custom
        //         SourceReferenceResolver so its global stubs don't shadow the real MacroGlobals.
        _shimRefresher = new IntelliSenseShimRefresher(
            globalFolderProvider: () => MacrosPaths.ResolveGlobalFolderOrFallback(
                MacrosOptions.Instance.GlobalMacrosFolder, out _),
            repoFolderProvider: () => _solutionTracker?.GetCurrentRepoMacrosFolder(),
            onError: (which, ex) => System.Diagnostics.Trace.WriteLine(
                $"Macros: failed to refresh {which} IntelliSense shim: {ex}"));
        _shimRefresher.AttachToTracker(_solutionTracker);
        _shimRefresher.RefreshGlobal();
        _shimRefresher.RefreshRepo(); // no-op if no solution is open

        // 2a-ter. One-time migration: any .csx macro recorded before this version was
        //         shipped has no `#load` directive pointing at the IntelliSense shim.
        //         Walk the global store and inject the directive in-place. Idempotent —
        //         files already carrying the directive are left untouched.
        try
        {
            var globalRoot = MacrosPaths.ResolveGlobalFolderOrFallback(
                MacrosOptions.Instance.GlobalMacrosFolder, out _);
            if (!string.IsNullOrWhiteSpace(globalRoot))
            {
                var result = MacroFileLoadDirectiveMigrator.Migrate(globalRoot);
                if (result.Updated > 0)
                {
                    System.Diagnostics.Trace.WriteLine(
                        $"Macros: migrated {result.Updated} global macro(s) to include IntelliSense #load directive.");
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not ThreadAbortException)
        {
            System.Diagnostics.Trace.WriteLine($"Macros: global migrator failed: {ex}");
        }

        // 2b. Wire the M4 trust-gate InfoBar.Subscribes to SolutionChanged on the tracker
        //     above and shows an InfoBar at the top of the editor whenever a solution opens
        //     that carries repo macros with auto-run triggers and is neither trusted nor
        //     blocked. Triggers stay dormant until the user makes a choice.
        _trustGateInfoBar = await TrustGateInfoBar.InitializeAsync(this, _solutionTracker);

        // 2c. Wire the trigger-hint InfoBar. Listens for .csx macro files being opened in
        //     the editor and shows a one-time tip about Manage Triggers.
        _triggerHintInfoBar = await TriggerHintInfoBar.InitializeAsync(this);

        // 3. Register IMacroPlayer so the play infrastructure has a resolvable producer.
        //    ScriptCompilationCache is package-lifetime (single instance on _scriptCache) so
        //    repeated plays of the same source hit the SHA-256 LRU without re-compiling.
        //    DTE2 is acquired here, before the lazy factory is invoked, to keep the factory
        //    trivially synchronous.
        var dte = await VS.GetRequiredServiceAsync<EnvDTE.DTE, DTE2>();
        this.AddService(
            typeof(IMacroPlayer),
            (_, _, _) => Task.FromResult<object>(
                new MacroPlayer(this.JoinableTaskFactory, _scriptCache, dte, new MacroPromptService())),
            promote: true);

        // 4. Register the priority command target so CommandObserver sees every shell command
        //    before any focused target gets it. The observer normally returns
        //    OLECMDERR_E_NOTSUPPORTED so the chain continues; the only exception is when a
        //    BeforeCommand macro vetoes the command via Trigger.CancelCommand().
        //    Registration must happen on the UI thread; AllowsBackgroundLoading means we may
        //    still be on the threadpool here.
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        // Construct the consecutive-failure tracker BEFORE the registry so the registry can
        // filter auto-disabled paths out of every Lookup. The tracker is shared between the
        // registry (filtering) and both dispatchers (recording success/failure on every play)
        // so a failing trigger macro disables itself across both command and event paths
        // simultaneously — that's the M4 auto-disable contract.
        _failureTracker = new MacroFailureTracker();

        // Construct the trigger registry over the shared store so the dispatcher can probe
        // O(1) per command. The kill-switch hook funnels MacrosOptions.DisableAllTriggers
        // back into the registry without taking a project reference on the options DLL.
        _triggerRegistry = new MacroTriggerRegistry(
            sharedStorage.Value,
            JoinableTaskFactory,
            disableAllTriggersProvider: () => MacrosOptions.Instance.DisableAllTriggers,
            failureTracker: _failureTracker);

        // 2a-quater. When a solution opens, wake the repo store's file watcher (the watcher
        //            start is one-shot at trigger-registry construction; if VS started with no
        //            solution open, the repo folder didn't exist yet, so the watcher was
        //            skipped). Also rebuild the trigger registry so pre-existing committed
        //            repo macro @trigger directives come online immediately.
        //            Ordering is critical: _shimRefresher (subscribed above) creates the repo
        //            folder first, then this handler finds it and starts the watcher.
        _solutionTracker.SolutionChanged += (_, _) =>
        {
            try
            {
                _repoMacroStore?.NotifySolutionChanged();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not ThreadAbortException)
            {
                System.Diagnostics.Trace.WriteLine($"Macros: failed to wake repo store on solution change: {ex}");
            }

            JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    if (_triggerRegistry is not null)
                    {
                        await _triggerRegistry.RefreshAsync();
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not ThreadAbortException)
                {
                    System.Diagnostics.Trace.WriteLine($"Macros: failed to refresh trigger registry on solution change: {ex}");
                }
            }).FileAndForget("Macros/Triggers/RefreshOnSolutionChange");
        };

        // Construct the dispatcher. The MacroPlayer here is a fresh instance — we cannot
        // resolve via VS.GetRequiredServiceAsync from inside the package's own
        // InitializeAsync without risking a deadlock against the service container that
        // hasn't finished registering us yet. The script cache and DTE are shared with the
        // proffered IMacroPlayer service so cache hits are still cross-instance.
        var dispatcherPlayer = new MacroPlayer(JoinableTaskFactory, _scriptCache, dte, new MacroPromptService());
        _commandTriggerDispatcher = new CommandTriggerDispatcher(
            _triggerRegistry,
            dispatcherPlayer,
            CommandNameCache.Instance,
            beforeTimeoutMsProvider: () => MacrosOptions.Instance.BeforeCommandTimeoutMs,
            JoinableTaskFactory,
            tracker: _failureTracker,
            guard: reentranceGuard);

        // Wire the bus → registry → player pipeline. Without this dispatcher the event bus
        // is dormant: VS.Events.* fires, but no listener turns the firing into a player
        // invocation. The dispatcher subscribes lazily — one bus subscription per canonical
        // name with at least one matching trigger, re-synced whenever the registry rebuilds.
        _eventTriggerDispatcher = new EventTriggerDispatcher(
            _triggerRegistry,
            _eventBus,
            dispatcherPlayer,
            JoinableTaskFactory,
            tracker: _failureTracker);

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

        // Subscribe to DocumentEvents.Opened so the recording session captures the full
        // file path whenever a document is opened during recording (Bug #2: file-open
        // path capture). The same VS.Events.DocumentEvents pattern used by TriggerHintInfoBar
        // fires for every document that gains its first editor frame — including files opened
        // via File > Open dialog where the IOleCommandTarget command carries no path arg.
        // The handler is a no-op when recording is not active (_recordingServiceRef may also
        // be null before the first IMacroService resolve, but recording can't start before
        // that happens, so no file opens are silently dropped in practice).
        _recordingDocumentEvents = VS.Events.DocumentEvents;
        _recordingDocumentEvents.Opened += OnDocumentOpenedForRecording;

        // 4. Register tool windows (scans this assembly for BaseToolWindow<T> subclasses).
        this.RegisterToolWindows();

        // 5. Wire the status bar to MacroService.StateChanged.
        await StatusBarObserver.InitializeAsync(this);

        // 5a. Inject the recording-active indicator into the LEFT side of the VS status bar.
        await RecordingStatusBarInjector.InitializeAsync(this);

        // 6. Wire UIContext activationso the VSCT VisibilityItems (Record / Stop buttons)
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
            // Clear the static handle before tearing down owned components so any late
            // background callback that still races against shutdown sees a null Instance
            // rather than a half-disposed package and skips its work cleanly.
            if (ReferenceEquals(Instance, this))
            {
                Instance = null;
            }

            _uiContextActivator?.Dispose();
            _recordingCapHandler?.Dispose();
            RecordingStatusBarInjector.Dispose();
            UnregisterCommandObserver();
            UnsubscribeCommandEvents();
            UnsubscribeRecordingDocumentEvents();
            // Dispose the event-trigger bridge BEFORE the bus / registry so its
            // subscription tokens unwire cleanly while their owners are still alive.
            _eventTriggerDispatcher?.Dispose();
            _triggerRegistry?.Dispose();
            _eventBus?.Dispose();
            _trustGateInfoBar?.Dispose();
            _triggerHintInfoBar?.Dispose();
            _shimRefresher?.Dispose();
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

    /// <summary>
    /// Handler forwarded from <c>VS.Events.DocumentEvents.Opened</c>. Pushes a
    /// <see cref="RecordedStep.FileOpenStep"/> into the active recording session when
    /// recording is in progress. No-op at all other times.
    /// </summary>
    /// <remarks>
    /// Fires on the UI thread. Failure is swallowed so a bad file path or an unexpected
    /// session state never tears down the VS document-event source for the whole session.
    /// </remarks>
    private void OnDocumentOpenedForRecording(string filePath)
    {
        try
        {
            var session = _recordingServiceRef?.CurrentSession;
            if (session is { IsCapturing: true })
            {
                session.OnFileOpen(filePath);
            }
        }
        catch
        {
            // Defensive: recording infrastructure must never crash the VS event system.
        }
    }

    /// <summary>
    /// Detaches the <c>DocumentEvents.Opened</c> subscription used to forward file opens
    /// into the recording session. Defensive against a partially-initialised package.
    /// </summary>
    private void UnsubscribeRecordingDocumentEvents()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_recordingDocumentEvents is not null)
        {
            try
            {
                _recordingDocumentEvents.Opened -= OnDocumentOpenedForRecording;
            }
            catch
            {
                // Shutdown path: toolkit event source may already be torn down.
            }

            _recordingDocumentEvents = null;
        }
    }

    private async Task RegisterCommandsAsync()
    {
        // Top-level commands
        await RecordCommand.InitializeAsync(this);
        await StopCommand.InitializeAsync(this);
        await PlayLastCommand.InitializeAsync(this);
        await ShowToolWindowCommand.InitializeAsync(this);
        await SaveAsCommand.InitializeAsync(this);
        await DeleteCommand.InitializeAsync(this);
        await RenameCommand.InitializeAsync(this);
        await EditCommand.InitializeAsync(this);
        await NewMacroCommand.InitializeAsync(this);
        await ToggleTriggersCommand.InitializeAsync(this);

        // Context-menu commands
        await PlayContextCommand.InitializeAsync(this);
        await DebugContextCommand.InitializeAsync(this);
        await EditContextCommand.InitializeAsync(this);
        await RenameContextCommand.InitializeAsync(this);
        await DeleteContextCommand.InitializeAsync(this);
        await OpenFolderContextCommand.InitializeAsync(this);
        await MoveToRepoCommand.InitializeAsync(this);
        await MoveToGlobalCommand.InitializeAsync(this);
        await CopyToRepoCommand.InitializeAsync(this);
        await CopyToGlobalCommand.InitializeAsync(this);
        await ManageTriggersContextCommand.InitializeAsync(this);
    }
}
