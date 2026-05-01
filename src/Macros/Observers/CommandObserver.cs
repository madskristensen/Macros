using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using EnvDTE;
using Macros.Commands;
using Macros.Engine;
using Macros.Triggers;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace Macros.Observers;

// =============================================================================
//  CommandObserver — three-purpose priority command target.
// =============================================================================
//
//  PURPOSE 1 (Phase A) — Recording capture.
//      When IMacroService.CurrentSession.IsCapturing is true, push the
//      (group, id, name) triple into the active session via
//      IRecordingSink.OnCommand. Pure observation: never throws, never alters
//      the command chain. This is the hot path and must stay allocation-light.
//
//  PURPOSE 2 (Phase B) — BeforeCommand trigger dispatch.
//      Invoke the optional CommandTriggerDispatcher.DispatchBefore hook. If any
//      matching BeforeCommand macro called Trigger.CancelCommand(), Phase B
//      returns true and Exec returns S_OK to claim the command — pre-empting
//      the rest of the command chain and effectively suppressing the
//      underlying VS command. A throwing or null dispatcher is fail-safe:
//      treat as "do not cancel" and continue.
//
//  PURPOSE 3 (Phase C) — Pass-through.
//      Return OLECMDERR_E_NOTSUPPORTED so the shell continues routing the
//      command to its real handler. This is the default outcome for every
//      command we do not suppress. There is no work in this phase beyond the
//      return value; it exists as a named step in the walkthrough so the
//      contract is impossible to misread.
//
//  AfterCommand dispatch is NOT a purpose of this class. IOleCommandTarget.Exec
//  runs strictly BEFORE the real handler and has no completion callback, so
//  AfterCommand triggers are wired to EnvDTE.CommandEvents.AfterExecute by
//  MacrosPackage instead.
// =============================================================================

/// <summary>
/// Priority command target observing every VS command. Has three responsibilities,
/// implemented as the explicit phases A / B / C in <see cref="Exec"/>:
/// <list type="number">
///   <item><description><b>Phase A — Recording capture.</b> Push commands into the
///   active <see cref="IMacroService.CurrentSession"/> when it is capturing.</description></item>
///   <item><description><b>Phase B — BeforeCommand dispatch.</b> Invoke
///   <see cref="CommandTriggerDispatcher.DispatchBefore"/> and cancel the underlying VS
///   command when any matching macro called <c>Trigger.CancelCommand()</c>.</description></item>
///   <item><description><b>Phase C — Pass-through.</b> Return
///   <c>OLECMDERR_E_NOTSUPPORTED</c> so the shell continues routing the command to its
///   real handler.</description></item>
/// </list>
/// AfterCommand triggers are intentionally NOT handled here — see the file header for the
/// rationale (priority targets have no completion callback).
/// </summary>
internal sealed class CommandObserver : IOleCommandTarget
{
    /// <summary>
    /// Commands we never want to record OR fan out as BeforeCommand triggers. Keep this
    /// small and explicit — overzealous filtering drops legitimate user actions;
    /// under-filtering pollutes recordings with shell noise. Tuneable; expand as we
    /// discover new offenders during dogfooding.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><c>cmdidMouseHover</c> (1037) — fires on every editor hover, never user-initiated.</description></item>
    /// <item><description>Toolbox navigation IDs (<c>cmdidToolboxAddTab</c> 884, <c>cmdidToolboxDeleteTab</c> 885,
    /// <c>cmdidToolboxRenameTab</c> 886, <c>cmdidToolboxAddItem</c> 889, <c>cmdidToolboxRemoveItem</c> 890,
    /// <c>cmdidToolboxRefresh</c> 891) — replaying these on machines with different toolboxes is meaningless.</description></item>
    /// <item><description><c>cmdidIdle</c> (294) — pumped by the shell continuously; should never appear in Exec but defensively skipped.</description></item>
    /// <item><description><c>cmdidEditMenu</c> (29) — top-level menu opener, captured separately if user actually clicks an item.</description></item>
    /// </list>
    /// </remarks>
    /// <summary>
    /// GUID for the VS 2000+ standard command set (GUID_VSStd2KCmdId). Commands in this
    /// group with well-known noise IDs are excluded from recording and trigger dispatch.
    /// </summary>
    private static readonly Guid VSStd2KCmdId = new("1496a755-94de-11d0-8c3f-00c04fc2aae2");

    private static readonly HashSet<(Guid Group, uint Id)> NoiseCommands = new()
    {
        (VSConstants.GUID_VSStandardCommandSet97, 1037), // cmdidMouseHover
        (VSConstants.GUID_VSStandardCommandSet97, 294),  // cmdidIdle
        (VSConstants.GUID_VSStandardCommandSet97, 29),   // cmdidEditMenu
        (VSConstants.GUID_VSStandardCommandSet97, 884),  // cmdidToolboxAddTab
        (VSConstants.GUID_VSStandardCommandSet97, 885),  // cmdidToolboxDeleteTab
        (VSConstants.GUID_VSStandardCommandSet97, 886),  // cmdidToolboxRenameTab
        (VSConstants.GUID_VSStandardCommandSet97, 889),  // cmdidToolboxAddItem
        (VSConstants.GUID_VSStandardCommandSet97, 890),  // cmdidToolboxRemoveItem
        (VSConstants.GUID_VSStandardCommandSet97, 891),  // cmdidToolboxRefresh
        // GUID_VSStd2KCmdId/1627 — editor window-focus sync command that fires on every
        // document tab activation. Has no DTE name and replaying it is a no-op; it
        // pollutes recordings with opaque GUID/ID lines.
        (VSStd2KCmdId, 1627u),
        // GUID_VSStandardCommandSet97/900 — fires when the File > Open dialog is invoked.
        // Has no args and no DTE name; the file path is captured separately via the
        // DocumentEvents.Opened subscription in MacrosPackage (Bug #2 fix) and emitted as
        // OpenFileAsync(@"path"). Recording this raw command alongside that would duplicate
        // the step without adding replay value.
        (VSConstants.GUID_VSStandardCommandSet97, 900u),
    };

    /// <summary>
    /// Self-skip GUID. Commands in the Macros.* command set are excluded from <em>both</em>
    /// Phase A (would cause record-the-recorder loops) and Phase B (a macro can't
    /// meaningfully trigger before/after its own invocation command — that's what direct
    /// playback APIs are for).
    /// </summary>
    private static readonly Guid MacrosCommandSetGuid = PackageGuids.guidMacrosPackageCmdSet;

    private readonly JoinableTaskFactory _jtf;

    /// <summary>
    /// Phase B injection point. <see langword="null"/> means "no BeforeCommand dispatching
    /// happens" (the observer becomes a pure recording sink). Wrapped as a delegate rather
    /// than the concrete <see cref="CommandTriggerDispatcher"/> type so unit tests can
    /// inject an arbitrary <see cref="Func{T1, T2, TResult}"/> without subclassing the
    /// (sealed) production dispatcher.
    /// </summary>
    private readonly Func<Guid, uint, bool>? _dispatchBefore;

    /// <summary>
    /// Phase A injection point. Returns the cached <see cref="IMacroService"/> instance,
    /// or <see langword="null"/> while the service hasn't been resolved yet (early after
    /// package load). Wrapped as a delegate so unit tests can supply a fake without
    /// touching <see cref="VS"/>'s static service container.
    /// </summary>
    private readonly Func<IMacroService?> _serviceAccessor;

    private DTE? _dte;

    // Backing state for the production-mode lazy resolver. Only touched from the
    // ResolveServiceLazily path; tests inject _serviceAccessor directly and never see these.
    private readonly object _serviceLock = new();
    private IMacroService? _service;
    private bool _resolveAttempted;

    /// <summary>Initializes a new <see cref="CommandObserver"/> for production use.</summary>
    /// <param name="jtf">The package's <see cref="JoinableTaskFactory"/>, used for the
    /// background <see cref="IMacroService"/> resolution.</param>
    /// <param name="dispatcher">Optional command-trigger dispatcher. When supplied,
    /// Phase B calls <see cref="CommandTriggerDispatcher.DispatchBefore"/> on every
    /// command and suppresses the underlying VS command if any <c>BeforeCommand</c>
    /// macro requested cancellation. <c>AfterCommand</c> dispatch is not handled here —
    /// it lives on the package's <c>EnvDTE.CommandEvents.AfterExecute</c> subscription,
    /// because the priority command target's <see cref="Exec"/> runs strictly
    /// <em>before</em> the actual command handler and has no callback for completion.</param>
    /// <exception cref="ArgumentNullException"><paramref name="jtf"/> is <see langword="null"/>.</exception>
    public CommandObserver(JoinableTaskFactory jtf, CommandTriggerDispatcher? dispatcher = null)
    {
        _jtf = jtf ?? throw new ArgumentNullException(nameof(jtf));
        _dispatchBefore = dispatcher is null ? null : dispatcher.DispatchBefore;
        _serviceAccessor = ResolveServiceLazily;
    }

    /// <summary>
    /// Test-only constructor that fully decouples the observer from the VS service container
    /// and the concrete <see cref="CommandTriggerDispatcher"/>. Both phases (recording sink
    /// and BeforeCommand dispatch) are injected as delegates so a unit test can drive Exec
    /// deterministically without spinning up a VS host.
    /// </summary>
    /// <param name="jtf">JoinableTaskFactory; tests may pass <c>new JoinableTaskContext().Factory</c>.</param>
    /// <param name="dispatchBefore">Phase B hook. <see langword="null"/> models the
    /// no-dispatcher case — Phase B becomes a no-op and <see cref="Exec"/> never returns
    /// <c>S_OK</c>.</param>
    /// <param name="serviceAccessor">Phase A hook. Returns the fake
    /// <see cref="IMacroService"/> (or <see langword="null"/> to model a not-yet-resolved
    /// service).</param>
    internal CommandObserver(
        JoinableTaskFactory jtf,
        Func<Guid, uint, bool>? dispatchBefore,
        Func<IMacroService?> serviceAccessor)
    {
        _jtf = jtf ?? throw new ArgumentNullException(nameof(jtf));
        _dispatchBefore = dispatchBefore;
        _serviceAccessor = serviceAccessor ?? throw new ArgumentNullException(nameof(serviceAccessor));
    }

    /// <inheritdoc />
    public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
    {
        // We are a passive observer — we never claim to handle anything, so the shell
        // continues querying downstream targets. Returning anything other than
        // NOTSUPPORTED here would freeze the shell into thinking the command is owned by us.
        return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
    }

    /// <inheritdoc />
    public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
    {
        // Priority command targets are invoked synchronously on the UI thread by VS;
        // assert that contract so the analyzer is satisfied and so a misconfigured
        // caller (e.g. routing Exec from a background thread) fails loudly instead of
        // silently corrupting recordings. Unit tests initialize ThreadHelper via a
        // collection fixture so this assertion succeeds out-of-process.
        ThreadHelper.ThrowIfNotOnUIThread();

        // Skip-list short-circuit: Macros.* and well-known noise commands bypass BOTH
        // Phase A (don't pollute recordings) and Phase B (don't meaningfully trigger
        // BeforeCommand for our own commands). They fall straight to Phase C.
        if (!IsSkipped(pguidCmdGroup, nCmdID))
        {
            // Phase A first: a recording is a faithful log of what the user attempted —
            // even a command later cancelled by a BeforeCommand validator is still a
            // user-initiated step worth capturing. Order matters here and is by design.
            ExecutePhaseA_Recording(pguidCmdGroup, nCmdID);

            if (ExecutePhaseB_BeforeCommand(pguidCmdGroup, nCmdID))
            {
                // Phase B claimed cancellation: return S_OK to suppress the underlying
                // command. We do NOT continue to Phase C — that would re-route the
                // command to its real handler, defeating the cancellation.
                return VSConstants.S_OK;
            }
        }

        // Phase C: pass-through. The shell continues to the next target in the chain.
        return ExecutePhaseC_PassThrough();
    }

    /// <summary>
    /// Returns <see langword="true"/> if the command is in the combined skip list and
    /// should bypass both Phase A and Phase B. Self-(Macros.*) commands and well-known
    /// noise commands (mouse hover, idle pump, toolbox tab navigation, top-level menu
    /// opens) are filtered here.
    /// </summary>
    private static bool IsSkipped(Guid group, uint id)
        => group == MacrosCommandSetGuid || NoiseCommands.Contains((group, id));

    // -------------------------------------------------------------------------
    //  Phase A — Recording capture.
    // -------------------------------------------------------------------------

    /// <summary>
    /// PHASE A: pushes the command into the active recording session, if any. Any
    /// exception (service resolution failure, sink throwing, name lookup throwing) is
    /// swallowed — this is the hot path and MUST never break the command chain. Failure
    /// to record a single command degrades the macro by one step; a thrown exception
    /// would tear down the priority command target subscription and break the IDE.
    /// </summary>
    private void ExecutePhaseA_Recording(Guid group, uint id)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var service = _serviceAccessor();
            if (service?.CurrentSession is not { IsCapturing: true } sink)
            {
                return;
            }

            sink.OnCommand(group, id, ResolveCommandName(group, id));
        }
        catch (Exception ex) when (LogAndContinue(ex))
        {
            // Swallowed by design — see method-level comment.
        }
    }

    // -------------------------------------------------------------------------
    //  Phase B — BeforeCommand trigger dispatch.
    // -------------------------------------------------------------------------

    /// <summary>
    /// PHASE B: invokes <see cref="CommandTriggerDispatcher.DispatchBefore"/> via the
    /// injected hook and returns its cancel decision. A <see langword="null"/> hook,
    /// or a hook that throws, both produce <see langword="false"/> — the fail-safe
    /// answer is "let the user's command run". Returning <see langword="true"/> is the
    /// only way <see cref="Exec"/> ever claims a command (S_OK).
    /// </summary>
    private bool ExecutePhaseB_BeforeCommand(Guid group, uint id)
    {
        var dispatch = _dispatchBefore;
        if (dispatch is null)
        {
            return false;
        }

        try
        {
            return dispatch(group, id);
        }
        catch (Exception ex) when (LogAndContinue(ex))
        {
            // Defensive: a throwing dispatcher must NEVER cancel the user's command.
            return false;
        }
    }

    // -------------------------------------------------------------------------
    //  Phase C — Pass-through.
    // -------------------------------------------------------------------------

    /// <summary>
    /// PHASE C: returns <c>OLECMDERR_E_NOTSUPPORTED</c> so the shell continues routing
    /// the command down the chain to its real handler. Exists as a named method so the
    /// three-phase walk in <see cref="Exec"/> reads symmetrically; there is no other work
    /// to do here.
    /// </summary>
    private static int ExecutePhaseC_PassThrough()
        => (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;

    /// <summary>
    /// Resolves the friendly DTE name for a command. Cache hits (the common case after
    /// <c>CommandNameCache.PrimeAsync</c> has run) return without any COM call. Cache
    /// misses fall back to <see cref="Commands.Item(object, int)"/>; failures (unknown
    /// command, no DTE, COM exception) return <see langword="null"/> so codegen can fall
    /// back to the raw <c>(group, id)</c> rendering.
    /// </summary>
    private string? ResolveCommandName(Guid group, uint id)
    {
        if (CommandNameCache.Instance.TryGetName((group, id), out var cached))
        {
            return cached;
        }

        try
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _dte ??= ServiceProvider.GlobalProvider.GetService(typeof(DTE)) as DTE;
            var dte = _dte;
            if (dte is null) return null;

            var cmd = dte.Commands.Item(group.ToString("B"), (int)id);
            var name = cmd?.Name;
            if (!string.IsNullOrEmpty(name))
            {
                CommandNameCache.Instance.TryAddName(group, id, name!);
            }
            return name;
        }
        catch
        {
            // DTE.Commands.Item throws for unknown commands — that is the common case for
            // editor/typing commands and shell internals. Return null and let codegen
            // decide how to render an unnamed (group, id) pair.
            return null;
        }
    }

    /// <summary>
    /// Production-mode lazy <see cref="IMacroService"/> accessor. Returns the cached
    /// instance once resolved; while resolution is in flight (or during a transient
    /// failure-and-retry window) returns <see langword="null"/>. The first call kicks off
    /// an async resolve through <see cref="MacrosPackage.Instance"/>'s own service
    /// container — NOT the global <see cref="VS"/> container — so the lookup hits the
    /// synchronously-populated package services even before promotion has finished. We
    /// don't block the UI thread synchronously because even one slow first-resolve would
    /// surface as a perceptible IDE hang.
    /// </summary>
    private IMacroService? ResolveServiceLazily()
    {
        var svc = _service;
        if (svc is not null)
        {
            return svc;
        }

        if (_resolveAttempted)
        {
            return null;
        }

        lock (_serviceLock)
        {
            if (_service is not null)
            {
                return _service;
            }

            if (_resolveAttempted)
            {
                return null;
            }

            _resolveAttempted = true;
            _ = _jtf.RunAsync(async () =>
            {
                try
                {
                    var package = MacrosPackage.Instance
                        ?? throw new InvalidOperationException(
                            "MacrosPackage is not loaded; cannot resolve IMacroService.");
                    var resolved = await package.GetServiceAsync(typeof(IMacroService)) as IMacroService
                        ?? throw new InvalidOperationException(
                            "IMacroService is not registered in the package container.");
                    Volatile.Write(ref _service, resolved);
                }
                catch (Exception ex)
                {
                    await ex.LogAsync();
                    // Allow a future Exec to retry — we don't want a transient
                    // package-load hiccup to permanently disable observation for the
                    // session.
                    _resolveAttempted = false;
                }
            });
        }

        return null;
    }

    private static bool LogAndContinue(Exception ex)
    {
        try
        {
            _ = ex.LogAsync();
        }
        catch
        {
            // Logging is best-effort; never let the logger itself break the command pipeline.
        }

        return true;
    }
}
