using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine;
using Macros.Engine.Player;
using Macros.Engine.Storage;
using Macros.Engine.Triggers;
using Macros.Commands;
using Macros.Lifecycle;
using Macros.Options;
using Macros.Trust;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace Macros.Triggers;

/// <summary>
/// Routes "VS command about to run" / "VS command just ran" notifications into the macro
/// player for any macros bound via <c>// @trigger BeforeCommand &lt;name&gt;</c> or
/// <c>// @trigger AfterCommand &lt;name&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>BeforeCommand</b> dispatch is <em>synchronous</em>: every matching macro runs to
/// completion (or hits the per-call timeout) before <see cref="DispatchBefore"/> returns.
/// If any macro called <c>Trigger.CancelCommand()</c> the method returns <see langword="true"/>
/// and the calling priority command target should suppress the underlying VS command. All
/// matching macros run regardless of cancellation status — a logging macro paired with a
/// validating macro must both fire even when the validator vetoes the command.
/// </para>
/// <para>
/// <b>AfterCommand</b> dispatch is <em>asynchronous and fire-and-forget</em>: the caller
/// returns immediately while the player work runs queued on the joinable task pool. The
/// underlying VS command has already executed by the time we get here, so cancellation
/// has no meaning and exceptions are swallowed (logged via FileAndForget).
/// </para>
/// <para>
/// <b>Failure tracking.</b> Each play (success or failure) is reported to the optional
/// <see cref="IMacroFailureTracker"/>. The tracker auto-disables macros that exceed the
/// consecutive-failure threshold; the registry's <c>FindByXxx</c> queries already filter
/// disabled paths out, so the dispatcher needs no extra guard.
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="DispatchBefore"/> assumes the caller is on the UI thread
/// (the only legitimate caller is the <c>IOleCommandTarget.Exec</c> hot path, which the
/// shell always invokes synchronously on the UI thread). It uses
/// <c>JoinableTaskFactory.RunAsync(...).Join()</c> so the player can hop threads via
/// <see cref="JoinableTaskFactory.SwitchToMainThreadAsync(CancellationToken)"/> without
/// risking a deadlock against the very UI thread we're blocking.
/// </para>
/// </remarks>
internal sealed class CommandTriggerDispatcher
{
    private readonly IMacroTriggerRegistry _registry;
    private readonly IMacroPlayer _player;
    private readonly CommandNameCache _nameCache;
    private readonly IMacroFailureTracker? _tracker;
    private readonly Func<int> _beforeTimeoutMsProvider;
    private readonly JoinableTaskFactory _jtf;
    private readonly Func<MacroEntry, string?> _sourceLoader;
    private readonly IMacroService? _macroService;
    private readonly TriggerReentranceGuard? _reentranceGuard;
    private readonly Func<MacroEntry, CancellationToken, Task<bool>> _trustGate;

    /// <summary>
    /// Initializes a new <see cref="CommandTriggerDispatcher"/>.
    /// </summary>
    /// <param name="registry">Index over the macro library; consulted via the O(1)
    /// <see cref="IMacroTriggerRegistry.HasBeforeCommand"/> /
    /// <see cref="IMacroTriggerRegistry.HasAfterCommand"/> probes before any allocation.</param>
    /// <param name="player">The compile-and-run pipeline shared with manual playback.</param>
    /// <param name="nameCache">Bidirectional <c>(group,id) ↔ name</c> cache primed at package
    /// load. Lookups that miss the cache short-circuit dispatch (the macro library indexes
    /// <em>by name</em>, so an unknown command can never match a binding).</param>
    /// <param name="beforeTimeoutMsProvider">Hot-read of the user-configurable BeforeCommand
    /// timeout from <c>MacrosOptions</c>. Wrapped in a <see cref="Func{TResult}"/> so the
    /// engine layer doesn't take a project reference on the options DLL.</param>
    /// <param name="jtf">JoinableTaskFactory used to run the async player synchronously
    /// from the UI thread (BeforeCommand) and to fire-and-forget background plays
    /// (AfterCommand).</param>
    /// <param name="tracker">Optional consecutive-failure tracker. Until the
    /// <c>m4-auto-disable</c> work lands this stays <see langword="null"/> and dispatch
    /// continues to behave correctly — the tracker is a UX wart suppressor, not part of
    /// the dispatch invariants.</param>
    /// <param name="sourceLoader">Optional override for reading the macro source. Defaults
    /// to <see cref="File.ReadAllText(string)"/>; tests inject a fake to avoid touching
    /// the filesystem. A return of <see langword="null"/> is treated as a load failure
    /// and recorded against the tracker.</param>
    /// <param name="guard">Optional reentrance guard. When supplied, recursive firings of the
    /// same command (same prefix+name) or depth-exceeded dispatches are suppressed silently.
    /// Pass <see langword="null"/> (default) for backward-compatible behaviour with no
    /// reentrance protection.</param>
    /// <param name="macroService">Optional service handle used to raise
    /// <c>TriggeredExecutionStarted</c> / <c>TriggeredExecutionEnded</c> for the status bar.</param>
    /// <param name="trustGate">Optional override for the per-entry trust check. When
    /// <see langword="null"/>, defaults to a pure <see cref="TrustGate.IsAllowed"/> check
    /// against <see cref="MacrosOptions.Instance"/> and the active solution path tracked by
    /// <see cref="SolutionContextTracker.Current"/>. Production wiring can supply a prompting
    /// callback; tests inject a deterministic async predicate to avoid touching VS-hosted statics.</param>
    public CommandTriggerDispatcher(
        IMacroTriggerRegistry registry,
        IMacroPlayer player,
        CommandNameCache nameCache,
        Func<int> beforeTimeoutMsProvider,
        JoinableTaskFactory jtf,
        IMacroFailureTracker? tracker = null,
        Func<MacroEntry, string?>? sourceLoader = null,
        TriggerReentranceGuard? guard = null,
        IMacroService? macroService = null,
        Func<MacroEntry, CancellationToken, Task<bool>>? trustGate = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _nameCache = nameCache ?? throw new ArgumentNullException(nameof(nameCache));
        _beforeTimeoutMsProvider = beforeTimeoutMsProvider ?? throw new ArgumentNullException(nameof(beforeTimeoutMsProvider));
        _jtf = jtf ?? throw new ArgumentNullException(nameof(jtf));
        _tracker = tracker;
        _sourceLoader = sourceLoader ?? DefaultSourceLoader;
        _reentranceGuard = guard;
        _macroService = macroService;
        _trustGate = trustGate ?? DefaultTrustGateAsync;
    }

    /// <summary>
    /// Synchronously runs every macro bound to <c>BeforeCommand &lt;commandName&gt;</c>
    /// for the supplied <c>(group, id)</c>. Returns <see langword="true"/> if any macro
    /// requested cancellation via <c>Trigger.CancelCommand()</c>; the caller should then
    /// suppress the underlying VS command.
    /// </summary>
    /// <param name="group">The OLE command group GUID.</param>
    /// <param name="id">The OLE command id.</param>
    /// <returns><see langword="true"/> when the underlying command should be suppressed;
    /// <see langword="false"/> otherwise (no triggers, no matches, no cancellation, or
    /// every matching macro errored — fail-safe is "let the command run").</returns>
    public bool DispatchBefore(Guid group, uint id)
    {
        if (!_nameCache.TryGetName((group, id), out var commandName) || string.IsNullOrEmpty(commandName))
        {
            return false;
        }

        // Cheap probe — short-circuits the per-command hot path when no BeforeCommand
        // bindings exist (the common case during normal IDE use).
        if (!_registry.HasBeforeCommand(commandName))
        {
            return false;
        }

        var matches = _registry.FindByBeforeCommand(commandName);
        if (matches.Count == 0)
        {
            return false;
        }

        // Trust gate: repo-scoped matches may trigger a one-time modal trust prompt before
        // automatic dispatch proceeds. Manual playback through the tool window remains unaffected.
        var allowed = FilterByTrust(matches);
        if (allowed.Count == 0)
        {
            return false;
        }
        matches = allowed;

        var key = $"before:{commandName}";
        if (_reentranceGuard != null && !_reentranceGuard.TryEnter(key, out var beforeScope))
        {
            return false;
        }
        else
        {
            beforeScope = null; // guard not in use — no scope to release
        }

        using (beforeScope)
        {
            var anyCancelled = false;
            var timeoutMs = ResolveTimeout();

            foreach (var match in matches)
            {
                var entry = match.Entry;
                var trigger = new CommandMacroTrigger(TriggerKind.BeforeCommand, commandName, DateTimeOffset.UtcNow);
                try
                {
                    var source = _sourceLoader(entry);
                    if (source is null)
                    {
                        // Source unavailable (deleted between enumerate and dispatch, or IO
                        // error). Treat as a failure for tracker bookkeeping, but do NOT let
                        // it cancel the user's command.
                        _tracker?.RecordFailure(entry.Path);
                        continue;
                    }

                    var triggeredArgs = new TriggeredExecutionEventArgs(entry.Name, commandName, TriggerKind.BeforeCommand);
                    if (_macroService is MacroService svc)
                    {
                        svc.RaiseTriggeredStarted(triggeredArgs);
                    }

                    try
                    {
                        using var cts = new CancellationTokenSource(timeoutMs);
                        var task = _jtf.RunAsync(async () =>
                        {
                            return await _player.PlayAsync(source, entry.Name, trigger, cts.Token, entry.Path).ConfigureAwait(true);
                        });

                        // Caller is on the UI thread — Join() pumps so the player's
                        // SwitchToMainThreadAsync hop completes without deadlocking.
                        var result = task.Join();

                        if (result.Success)
                        {
                            _tracker?.RecordSuccess(entry.Path);
                        }
                        else
                        {
                            _tracker?.RecordFailure(entry.Path);
                        }

                        if (trigger.CommandCancelled)
                        {
                            anyCancelled = true;
                        }
                    }
                    finally
                    {
                        if (_macroService is MacroService svc2)
                        {
                            svc2.RaiseTriggeredEnded(triggeredArgs);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Timeout expired — log + count as a failure, but do NOT cancel the
                    // command. A buggy/slow macro must never break the user's IDE.
                    _tracker?.RecordFailure(entry.Path);
                }
                catch (Exception)
                {
                    // Defensive: any other exception (player bug, source load racing a
                    // delete, etc.) is swallowed for the same fail-safe reason.
                    _tracker?.RecordFailure(entry.Path);
                }
            }

            return anyCancelled;
        }
    }

    /// <summary>
    /// Asynchronously enqueues every macro bound to <c>AfterCommand &lt;commandName&gt;</c>
    /// for the supplied <c>(group, id)</c>. Returns immediately; macros run on background
    /// joinable tasks and any errors are reported via FileAndForget.
    /// </summary>
    /// <param name="group">The OLE command group GUID.</param>
    /// <param name="id">The OLE command id.</param>
    public void DispatchAfter(Guid group, uint id)
    {
        if (!_nameCache.TryGetName((group, id), out var commandName) || string.IsNullOrEmpty(commandName))
        {
            return;
        }

        if (!_registry.HasAfterCommand(commandName))
        {
            return;
        }

        var matches = _registry.FindByAfterCommand(commandName);
        if (matches.Count == 0)
        {
            return;
        }

        var allowed = FilterByTrust(matches);
        if (allowed.Count == 0)
        {
            return;
        }
        matches = allowed;

        var key = $"after:{commandName}";

        foreach (var match in matches)
        {
            var entry = match.Entry;
            var guard = _reentranceGuard;
            _jtf.RunAsync(async () =>
            {
                if (guard != null && !guard.TryEnter(key, out var afterScope))
                {
                    return;
                }
                else
                {
                    afterScope = null; // guard not in use
                }

                using (afterScope)
                {
                    var trigger = new CommandMacroTrigger(TriggerKind.AfterCommand, commandName, DateTimeOffset.UtcNow);
                    try
                    {
                        var source = _sourceLoader(entry);
                        if (source is null)
                        {
                            _tracker?.RecordFailure(entry.Path);
                            return;
                        }

                        var triggeredArgs = new TriggeredExecutionEventArgs(entry.Name, commandName, TriggerKind.AfterCommand);
                        if (_macroService is MacroService svc)
                        {
                            svc.RaiseTriggeredStarted(triggeredArgs);
                        }

                        try
                        {
                            var result = await _player.PlayAsync(source, entry.Name, trigger, CancellationToken.None, entry.Path).ConfigureAwait(true);
                            if (result.Success)
                            {
                                _tracker?.RecordSuccess(entry.Path);
                            }
                            else
                            {
                                _tracker?.RecordFailure(entry.Path);
                            }
                        }
                        finally
                        {
                            if (_macroService is MacroService svc2)
                            {
                                svc2.RaiseTriggeredEnded(triggeredArgs);
                            }
                        }
                    }
                    catch (Exception)
                    {
                        _tracker?.RecordFailure(entry.Path);
                    }
                }
            }).FileAndForget("Macros/AfterCommand");
        }
    }

    private IReadOnlyList<TriggerMatch> FilterByTrust(IReadOnlyList<TriggerMatch> matches)
    {
        if (matches.Count == 0) return matches;
        return _jtf.Run(() => FilterByTrustAsync(matches, CancellationToken.None));
    }

    private async Task<IReadOnlyList<TriggerMatch>> FilterByTrustAsync(IReadOnlyList<TriggerMatch> matches, CancellationToken cancellationToken)
    {
        List<TriggerMatch>? kept = null;
        for (int i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            bool ok;
            try { ok = await _trustGate(m.Entry, cancellationToken).ConfigureAwait(true); }
            catch { ok = false; }   // a throwing trust gate fails closed.

            if (ok)
            {
                kept?.Add(m);
            }
            else if (kept is null)
            {
                kept = new List<TriggerMatch>(matches.Count);
                for (int j = 0; j < i; j++) kept.Add(matches[j]);
            }
        }
        return kept ?? matches;
    }

    private static Task<bool> DefaultTrustGateAsync(MacroEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            var soln = SolutionContextTracker.Current?.GetCurrentSolutionPath();
            return Task.FromResult(TrustGate.IsAllowed(entry, MacrosOptions.Instance, soln));
        }
        catch
        {
            // Fail-safe: when option/static state isn't reachable (e.g. unit-test host
            // without VS settings store), only Global entries are allowed to dispatch.
            return Task.FromResult(entry?.Scope == MacroScope.Global);
        }
    }

    private int ResolveTimeout()
    {
        try
        {
            var ms = _beforeTimeoutMsProvider();
            // Clamp to a sane minimum so a misconfigured 0/negative timeout doesn't
            // immediately cancel every play before it can compile.
            return ms < 1 ? 1 : ms;
        }
        catch
        {
            // A throwing options accessor must never break dispatch. Fall back to the
            // documented default (matches MacrosOptions.BeforeCommandTimeoutMs default).
            return 2000;
        }
    }

    private static string? DefaultSourceLoader(MacroEntry entry)
    {
        try
        {
            return File.ReadAllText(entry.Path);
        }
        catch
        {
            return null;
        }
    }
}
