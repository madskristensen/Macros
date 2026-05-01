using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using EnvDTE;
using Macros.Commands;
using Macros.Engine;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace Macros.Observers;

/// <summary>
/// Passive priority command target that observes every command flowing through the VS shell
/// and feeds the interesting ones into the active recording session.
/// </summary>
/// <remarks>
/// <para>
/// Registered with <see cref="IVsRegisterPriorityCommandTarget"/> so we sit at the head of the
/// command chain and see <em>every</em> dispatched command before any focused command target
/// gets a crack at it. The observer is strictly passive: <see cref="Exec"/> always returns
/// <c>OLECMDERR_E_NOTSUPPORTED</c> so the shell continues routing the command to the real
/// handler. Returning anything else here would silently swallow the command — exactly the
/// kind of bug that would take days to track down.
/// </para>
/// <para>
/// Threading: priority command targets are always invoked on the UI thread by VS, so
/// <see cref="DTE"/> access is safe without an explicit thread switch. The first-time
/// <see cref="IMacroService"/> resolution hops to a background-friendly async path.
/// </para>
/// </remarks>
internal sealed class CommandObserver : IOleCommandTarget
{
    /// <summary>
    /// Commands we never want to record. Keep this small and explicit — overzealous filtering
    /// drops legitimate user actions; under-filtering pollutes the macro with shell noise.
    /// Tuneable; expand as we discover new offenders during dogfooding.
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
    };

    private static readonly Guid MacrosCommandSetGuid = new(PackageGuids.CommandSetGuidString);

    private readonly JoinableTaskFactory _jtf;
    private readonly object _serviceLock = new();
    private IMacroService? _service;
    private DTE? _dte;
    private bool _resolveAttempted;

    /// <summary>Initializes a new <see cref="CommandObserver"/>.</summary>
    /// <param name="jtf">The package's <see cref="JoinableTaskFactory"/>, used for the first
    /// asynchronous <see cref="IMacroService"/> resolution.</param>
    /// <exception cref="ArgumentNullException"><paramref name="jtf"/> is <see langword="null"/>.</exception>
    public CommandObserver(JoinableTaskFactory jtf)
    {
        _jtf = jtf ?? throw new ArgumentNullException(nameof(jtf));
    }

    /// <inheritdoc />
    public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
    {
        // We are a passive observer — we never claim to handle anything, so the shell continues
        // querying downstream targets. Returning anything other than NOTSUPPORTED here would
        // freeze the shell into thinking the command is owned by us.
        return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
    }

    /// <inheritdoc />
    public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            ObserveCommand(pguidCmdGroup, nCmdID);
        }
        catch (Exception ex) when (LogAndContinue(ex))
        {
            // Swallow — observation must NEVER prevent the real command from running.
        }

        return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
    }

    private void ObserveCommand(Guid group, uint id)
    {
        // Priority command targets are invoked synchronously on the UI thread by the shell;
        // assert that contract so the analyzer is happy and so a caller wiring us up wrong
        // (e.g. routing Exec from a background thread in tests) fails loudly instead of
        // silently corrupting recordings.
        ThreadHelper.ThrowIfNotOnUIThread();

        // Cheap bail-outs first — these are hot paths called for every keystroke / menu hit.
        if (group == MacrosCommandSetGuid)
        {
            return; // self-capture would record Stop / Record / etc. into the very macro being recorded
        }

        if (NoiseCommands.Contains((group, id)))
        {
            return;
        }

        var sink = TryGetSink();
        if (sink is null || !sink.IsCapturing)
        {
            return;
        }

        var name = ResolveCommandName(group, id);
        sink.OnCommand(group, id, name);
    }

    private IRecordingSinkLike? TryGetSink()
    {
        // Resolve once and cache. We don't await here — Exec is sync and on the UI thread, so
        // we either have the service or we miss the very first commands while the package
        // finishes loading. That's acceptable; the package is auto-loaded on shell startup.
        var svc = _service;
        if (svc is not null)
        {
            return Wrap(svc);
        }

        if (_resolveAttempted)
        {
            return null;
        }

        lock (_serviceLock)
        {
            if (_service is not null)
            {
                return Wrap(_service);
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
                    var resolved = await VS.GetRequiredServiceAsync<IMacroService, IMacroService>();
                    Volatile.Write(ref _service, resolved);
                }
                catch (Exception ex)
                {
                    await ex.LogAsync();
                    // Allow a future Exec to retry — we don't want a transient package-load
                    // hiccup to permanently disable observation for the session.
                    _resolveAttempted = false;
                }
            });
        }

        return null;
    }

    private static IRecordingSinkLike Wrap(IMacroService svc) => new SinkAdapter(svc);

    private string? ResolveCommandName(Guid group, uint id)
    {
        // Fast path: cache hit (no COM call needed).
        var cached = CommandNameCache.Instance.Lookup(group, id);
        if (cached is not null) return cached;

        // Slow path: fall back to DTE for commands registered after priming (e.g. dynamic commands).
        try
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _dte ??= ServiceProvider.GlobalProvider.GetService(typeof(DTE)) as DTE;
            var dte = _dte;
            if (dte is null) return null;

            var cmd = dte.Commands.Item(group.ToString("B"), (int)id);
            var name = cmd?.Name;
            if (name is not null)
            {
                CommandNameCache.Instance.TryAddName(group, id, name);
            }
            return name;
        }
        catch
        {
            // DTE.Commands.Item throws for unknown commands — that is the common case for
            // editor/typing commands and shell internals. Return null and let codegen decide
            // how to render an unnamed (group, id) pair.
            return null;
        }
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

    /// <summary>
    /// Tiny indirection so the observer's hot path doesn't need to read the IMacroService
    /// interface twice (once to fetch the sink, once to peek IsCapturing). Also makes
    /// <see cref="TryGetSink"/> trivially mockable in unit tests if a future test wants it.
    /// </summary>
    private interface IRecordingSinkLike
    {
        bool IsCapturing { get; }
        void OnCommand(Guid group, uint id, string? name);
    }

    private sealed class SinkAdapter : IRecordingSinkLike
    {
        private readonly IMacroService _svc;
        public SinkAdapter(IMacroService svc) => _svc = svc;
        public bool IsCapturing => _svc.CurrentSession?.IsCapturing == true;
        public void OnCommand(Guid group, uint id, string? name) => _svc.CurrentSession?.OnCommand(group, id, name);
    }
}
