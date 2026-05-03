using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Macros.Engine.Player;
using Macros.Engine.Storage;
using Macros.Engine.Triggers;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace Macros.Engine.Scripting;

/// <summary>
/// The static verb library that the C# code generator (<c>m2-csharp-codegen</c>) emits calls into.
/// Every method is asynchronous (<c>*Async</c> / returns <see cref="Task"/>) and every method
/// switches to the Visual Studio UI thread internally before touching DTE / shell services.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sync vs async.</b> All helpers are async. Generated macros therefore look like
/// <c>await TypeAsync("foo");</c>. Going async-only avoids two classes of pitfall:
/// (a) deadlocks when scripts are launched from a background thread and (b) sync-over-async
/// hangs inside the toolkit's own service-provider plumbing. The cost — every call site needs
/// <c>await</c> — is borne by the generator, not the user.
/// </para>
/// <para>
/// <b>Cancellation.</b> Every method takes an optional <see cref="CancellationToken"/> defaulting
/// to <see cref="CancellationToken.None"/>. The macro player (M3) sets
/// <see cref="MacroGlobals.Context"/>'s <see cref="IMacroContext.Cancellation"/> before
/// invocation; codegen explicitly forwards <c>Context.Cancellation</c> at every call site so a
/// single Esc cancels mid-macro at the next helper await.
/// </para>
/// <para>
/// <b>Ambient globals.</b> Helpers read the active <see cref="MacroGlobals"/> from
/// <see cref="CurrentGlobals"/>, an <see cref="AsyncLocal{T}"/> that the player sets immediately
/// before <c>script.RunAsync</c>. <see cref="AsyncLocal{T}"/> is used (rather than
/// <see cref="ThreadStaticAttribute"/>) so the value flows across the
/// <c>SwitchToMainThreadAsync</c> hop and any user-scheduled <c>Task.Run</c> within the script.
/// </para>
/// </remarks>
public static class Helpers
{
    /// <summary>
    /// Ambient slot the player writes to before invoking <c>script.RunAsync</c>. Internal so the
    /// player and unit tests can set it; user code never sees it directly.
    /// </summary>
    internal static readonly AsyncLocal<MacroGlobals?> CurrentGlobals = new();

    /// <summary>Maximum nesting depth for <see cref="RunMacroAsync"/> calls.</summary>
    /// <remarks>
    /// Matches <see cref="TriggerReentranceGuard.MaxDepth"/> so the helper-side budget is the
    /// same shape users already learn for trigger composition. Note that the two budgets are
    /// <em>independent</em>: a triggered macro that nests three more <see cref="RunMacroAsync"/>
    /// calls is allowed by design — the trigger system already guards its own pipeline.
    /// </remarks>
    internal const int MaxNestedRunMacroDepth = 3;

    /// <summary>
    /// Tracks the resolved <c>(scope, name)</c> identities currently on the call stack so a
    /// macro cannot directly or transitively invoke itself. <see cref="AsyncLocal{T}"/>
    /// flows the active set across <see langword="await"/> hops. Entries are case-insensitive.
    /// </summary>
    private static readonly AsyncLocal<HashSet<string>?> ActiveRunKeys = new();

    /// <summary>
    /// Inserts <paramref name="text"/> at the active document's caret / selection insertion point.
    /// </summary>
    /// <param name="text">Verbatim text to insert. Must not be <see langword="null"/>.</param>
    /// <param name="cancellation">Token honoured before switching to the UI thread.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// No ambient <see cref="MacroGlobals"/> is set (the helper was called outside a macro
    /// invocation), or the active document has no <see cref="TextSelection"/>.
    /// </exception>
    public static async Task TypeAsync(string text, CancellationToken cancellation = default)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        cancellation.ThrowIfCancellationRequested();
        _ = RequireGlobals();

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellation);

        var sel = RequireTextSelection();
        sel.Insert(text, (int)vsInsertFlags.vsInsertFlagsInsertAtStart);
    }

    /// <summary>
    /// Moves the active document's caret to the supplied 1-based line and column.
    /// </summary>
    /// <param name="line">1-based line number.</param>
    /// <param name="column">1-based column number.</param>
    /// <param name="cancellation">Token honoured before switching to the UI thread.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="line"/> or <paramref name="column"/> is less than 1.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// No ambient <see cref="MacroGlobals"/> is set, or the active document has no
    /// <see cref="TextSelection"/>.
    /// </exception>
    public static async Task MoveCaretAsync(int line, int column, CancellationToken cancellation = default)
    {
        if (line < 1) throw new ArgumentOutOfRangeException(nameof(line), line, "Line must be 1-based.");
        if (column < 1) throw new ArgumentOutOfRangeException(nameof(column), column, "Column must be 1-based.");
        cancellation.ThrowIfCancellationRequested();
        _ = RequireGlobals();

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellation);

        var sel = RequireTextSelection();
        sel.MoveToLineAndOffset(line, column);
    }

    /// <summary>
    /// Selects the range from (<paramref name="startLine"/>, <paramref name="startCol"/>) to
    /// (<paramref name="endLine"/>, <paramref name="endCol"/>). All coordinates are 1-based.
    /// </summary>
    /// <param name="startLine">1-based line number where the selection begins.</param>
    /// <param name="startCol">1-based column where the selection begins.</param>
    /// <param name="endLine">1-based line number where the selection ends (inclusive of caret position).</param>
    /// <param name="endCol">1-based column where the selection ends.</param>
    /// <param name="cancellation">Token honoured before switching to the UI thread.</param>
    /// <exception cref="ArgumentOutOfRangeException">Any coordinate is less than 1.</exception>
    /// <exception cref="InvalidOperationException">
    /// No ambient <see cref="MacroGlobals"/> is set, or the active document has no
    /// <see cref="TextSelection"/>.
    /// </exception>
    public static async Task SelectAsync(int startLine, int startCol, int endLine, int endCol, CancellationToken cancellation = default)
    {
        if (startLine < 1) throw new ArgumentOutOfRangeException(nameof(startLine), startLine, "Coordinate must be 1-based.");
        if (startCol < 1) throw new ArgumentOutOfRangeException(nameof(startCol), startCol, "Coordinate must be 1-based.");
        if (endLine < 1) throw new ArgumentOutOfRangeException(nameof(endLine), endLine, "Coordinate must be 1-based.");
        if (endCol < 1) throw new ArgumentOutOfRangeException(nameof(endCol), endCol, "Coordinate must be 1-based.");
        cancellation.ThrowIfCancellationRequested();
        _ = RequireGlobals();

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellation);

        var sel = RequireTextSelection();
        sel.MoveToLineAndOffset(startLine, startCol);
        sel.MoveToLineAndOffset(endLine, endCol, Extend: true);
    }

    /// <summary>
    /// Executes a Visual Studio command by its DTE name (e.g. <c>"Edit.Find"</c>).
    /// </summary>
    /// <param name="name">Fully-qualified DTE command name. Must not be null or whitespace.</param>
    /// <param name="args">Optional command argument string. Empty by default.</param>
    /// <param name="cancellation">Token honoured before switching to the UI thread.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or whitespace.</exception>
    /// <exception cref="InvalidOperationException">No ambient <see cref="MacroGlobals"/> is set.</exception>
    public static async Task ExecuteCommandAsync(string name, string args = "", CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Command name must be non-empty.", nameof(name));
        }

        cancellation.ThrowIfCancellationRequested();
        DTE2 dte = RequireGlobals().DTE;

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellation);

        dte.ExecuteCommand(name, args ?? string.Empty);
    }

    /// <summary>
    /// Posts a command identified by its <see cref="Guid"/> command-set and numeric ID via
    /// <see cref="IVsUIShell.PostExecCommand"/>. Use this when the command has no DTE name
    /// (e.g. internal package commands recorded by GUID/ID pair).
    /// </summary>
    /// <param name="group">The command set GUID.</param>
    /// <param name="id">The numeric command ID within <paramref name="group"/>.</param>
    /// <param name="args">
    /// Optional argument value (variant-style). When <see langword="null"/>, an empty string is
    /// passed because <see cref="IVsUIShell.PostExecCommand"/>'s <c>pvaIn</c> parameter is
    /// declared <c>ref object</c> and rejects literal nulls.
    /// </param>
    /// <param name="cancellation">Token honoured before switching to the UI thread.</param>
    /// <exception cref="InvalidOperationException">
    /// No ambient <see cref="MacroGlobals"/> is set, or <see cref="SVsUIShell"/> could not be
    /// resolved from the global service provider (host is not a running Visual Studio).
    /// </exception>
    public static async Task RunCommandAsync(Guid group, uint id, object? args = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();

        // Validate the ambient globals contract on the calling thread so misuse fails fast even
        // before we attempt to marshal to the UI thread.
        _ = RequireGlobals();

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellation);

        var shell = (IVsUIShell?)Package.GetGlobalService(typeof(SVsUIShell))
            ?? throw new InvalidOperationException(
                "IVsUIShell is not available; RunCommandAsync requires a running Visual Studio host.");

        // PostExecCommand uses the OLE-style command dispatch under the hood (it ultimately
        // calls IOleCommandTarget.Exec). We prefer IVsUIShell over reaching for IOleCommandTarget
        // directly because the shell handles target routing (focus context, command priority,
        // active selection container) for us — which is what a macro replay actually wants.
        object input = args ?? string.Empty;
        ErrorHandler.ThrowOnFailure(shell.PostExecCommand(ref group, id, 0u, ref input));
    }

    /// <summary>
    /// Opens the file at <paramref name="path"/> in the Visual Studio editor, exactly as if
    /// the user had selected it via File → Open → File… The document is made the active
    /// editor window. If the file is already open, VS brings its window to the foreground.
    /// </summary>
    /// <param name="path">Full absolute path of the file to open. Must not be <see langword="null"/>.</param>
    /// <param name="cancellation">Token honoured before switching to the UI thread.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">No ambient <see cref="MacroGlobals"/> is set.</exception>
    public static async Task OpenFileAsync(string path, CancellationToken cancellation = default)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        cancellation.ThrowIfCancellationRequested();
        DTE2 dte = RequireGlobals().DTE;

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellation);

        // Validate before delegating to DTE — ItemOperations.OpenFile rejects non-paths
        // (e.g. RDT_Mk.* monikers a stale recording might still contain) with an opaque
        // E_INVALIDARG. Surface a useful error instead so users editing macros by hand
        // see what went wrong without digging through stack traces.
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path must be a non-empty absolute file path.", nameof(path));
        }
        if (!System.IO.Path.IsPathRooted(path))
        {
            throw new ArgumentException(
                $"OpenFileAsync requires an absolute file path; got '{path}'. " +
                "If this came from a recording, re-record the macro — the recorder now filters " +
                "pseudo-document monikers.",
                nameof(path));
        }

        dte.ItemOperations.OpenFile(path);
    }

    /// <summary>
    /// Closes the open document at <paramref name="path"/>, if any. No-op when the file is
    /// not currently open. Replays the user's "click X on the document tab" action.
    /// </summary>
    /// <param name="path">Full absolute path of the file to close.</param>
    /// <param name="cancellation">Token honoured before switching to the UI thread.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> isn't an absolute path.</exception>
    /// <exception cref="InvalidOperationException">No ambient <see cref="MacroGlobals"/> is set.</exception>
    public static async Task CloseFileAsync(string path, CancellationToken cancellation = default)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path must be a non-empty absolute file path.", nameof(path));
        }
        if (!System.IO.Path.IsPathRooted(path))
        {
            throw new ArgumentException(
                $"CloseFileAsync requires an absolute file path; got '{path}'.",
                nameof(path));
        }

        cancellation.ThrowIfCancellationRequested();
        DTE2 dte = RequireGlobals().DTE;

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellation);

        // Walk the open documents and close the first match. DTE.Documents is keyed by
        // FullName which is the absolute path; comparison is case-insensitive on NTFS.
        // If the file isn't open we silently no-op — replays must not fail just because
        // the user already closed the tab manually before invoking the macro.
        foreach (Document doc in dte.Documents)
        {
            string? fullName;
            try { fullName = doc.FullName; }
            catch { continue; }   // some documents throw on FullName (read-only memory docs, etc.)

            if (string.Equals(fullName, path, StringComparison.OrdinalIgnoreCase))
            {
                doc.Close(vsSaveChanges.vsSaveChangesPrompt);
                return;
            }
        }
    }

    /// <summary>
    /// Shows a Visual Studio input dialog and returns the user's input.
    /// If the user cancels, returns the default value.
    /// </summary>
    /// <param name="label">The prompt label shown to the user.</param>
    /// <param name="defaultValue">The pre-filled default value (returned on cancel).</param>
    /// <returns>The user's input string, or <paramref name="defaultValue"/> if cancelled.</returns>
    public static async Task<string> PromptAsync(string label, string defaultValue = "")
    {
        if (label is null) throw new ArgumentNullException(nameof(label));
        if (defaultValue is null) throw new ArgumentNullException(nameof(defaultValue));

        MacroGlobals globals = RequireGlobals();
        globals.Context.Cancellation.ThrowIfCancellationRequested();

        Microsoft.VisualStudio.Threading.JoinableTaskFactory jtf = globals.UiThreadFactory ?? ThreadHelper.JoinableTaskFactory;
        await jtf.SwitchToMainThreadAsync(globals.Context.Cancellation);

        IMacroPromptService promptService = globals.PromptService
            ?? throw new InvalidOperationException(
                "No prompt service is configured. PromptAsync requires a running Visual Studio host.");

        return await promptService.PromptAsync(label, defaultValue);
    }

    /// <summary>Recorded macros never emit
    /// this — it exists for user-authored scripts that need a deliberate pause (e.g. waiting for
    /// an asynchronous editor command to settle).
    /// </summary>
    /// <param name="milliseconds">Non-negative wait duration.</param>
    /// <param name="cancellation">Token that aborts the wait early.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="milliseconds"/> is negative.
    /// </exception>
    public static Task WaitAsync(int milliseconds, CancellationToken cancellation = default)
    {
        if (milliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(milliseconds), milliseconds, "Wait must be non-negative.");
        }

        return Task.Delay(milliseconds, cancellation);
    }

    /// <summary>
    /// Loads another macro by name and runs it inline within the current macro. Repo macros
    /// take precedence over global macros of the same name (matches the tool window's
    /// repo-wins semantics). Use this to compose a long sequence out of small reusable pieces.
    /// </summary>
    /// <param name="name">
    /// The macro name (file stem, no <c>.csx</c> extension). Validated by the underlying store.
    /// </param>
    /// <param name="cancellation">
    /// Token that cancels the nested execution. Linked with <see cref="IMacroContext.Cancellation"/>
    /// so an Esc on the parent also cancels the child.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or whitespace.</exception>
    /// <exception cref="InvalidOperationException">
    /// The ambient macro context is missing a store / player (helper called outside a macro
    /// invocation), the macro could not be found, the call would create a cycle (A → A or
    /// A → B → A), or nesting depth would exceed <see cref="MaxNestedRunMacroDepth"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The nested macro runs with a fresh <see cref="ManualMacroTrigger"/> so the called macro
    /// sees <c>Trigger.IsManual == true</c> regardless of why the parent was invoked. This is
    /// deliberate — the call site is "deliberate composition", not the original event.
    /// </para>
    /// <para>
    /// The nested macro's compile / runtime errors propagate as exceptions so the parent
    /// macro can <c>try / catch</c> them. A cancellation surfaces as
    /// <see cref="OperationCanceledException"/>; a compile error surfaces as
    /// <see cref="InvalidOperationException"/> with the diagnostic text in the message.
    /// </para>
    /// </remarks>
    public static async Task RunMacroAsync(string name, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Macro name must be non-empty.", nameof(name));
        }

        MacroGlobals globals = RequireGlobals();
        IMacroStore store = globals.Store
            ?? throw new InvalidOperationException(
                "RunMacroAsync requires a macro store; the current macro context was created without one.");
        IMacroPlayer player = globals.Player
            ?? throw new InvalidOperationException(
                "RunMacroAsync requires a macro player; the current macro context was created without one.");

        // Resolve macro source: repo first when a solution is open, then global. We deliberately
        // try repo even if it would throw for missing solution — IMacroStore implementations are
        // expected to skip the repo half cleanly when no solution is open.
        (string? source, MacroScope resolvedScope) = await ResolveMacroAsync(store, name, cancellation).ConfigureAwait(false);
        if (source is null)
        {
            throw new InvalidOperationException(
                $"Macro '{name}' was not found in either the repo or global scope.");
        }

        // Resolve the on-disk path for the nested macro so the player can root #load
        // directives at the macro's folder and surface the path in diagnostics. Best-effort
        // — a throw here (e.g. invalid name guarded inside the store) falls through to a
        // null path, which keeps playback working without the path-aware features.
        string? csxFilePath = null;
        try { csxFilePath = store.GetMacroPath(name, resolvedScope); }
        catch { /* path is informational; player handles null cleanly */ }

        // Re-entrance + depth guard, keyed by resolved (scope, name) so a global "fmt" calling a
        // repo "fmt" is allowed; a self-call (same scope+name) is not.
        string key = $"{resolvedScope}:{name}".ToUpperInvariant();
        HashSet<string> currentSet = ActiveRunKeys.Value ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (currentSet.Count >= MaxNestedRunMacroDepth)
        {
            throw new InvalidOperationException(
                $"RunMacroAsync nesting depth exceeded ({MaxNestedRunMacroDepth}); refusing to run '{name}'.");
        }

        if (currentSet.Contains(key))
        {
            throw new InvalidOperationException(
                $"RunMacroAsync cycle detected: macro '{name}' (scope={resolvedScope}) is already running on the call stack.");
        }

        var nextSet = new HashSet<string>(currentSet, StringComparer.OrdinalIgnoreCase) { key };
        HashSet<string>? previousSet = ActiveRunKeys.Value;
        ActiveRunKeys.Value = nextSet;

        // Link the ambient context's cancellation with the caller-supplied one so an Esc on
        // the parent (which trips Context.Cancellation) also cancels the child.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, globals.Context.Cancellation);

        try
        {
            MacroPlayResult result = await player.PlayAsync(
                source,
                name,
                ManualMacroTrigger.Instance,
                linked.Token,
                csxFilePath: csxFilePath).ConfigureAwait(false);

            if (result.Success)
            {
                return;
            }

            // Translate failed result into an exception so callers can catch normally rather
            // than have nested failures silently swallowed.
            if (result.RuntimeError is OperationCanceledException oce)
            {
                throw oce;
            }

            if (result.RuntimeError is not null)
            {
                throw new InvalidOperationException(
                    $"Nested macro '{name}' failed: {result.RuntimeError.Message}",
                    result.RuntimeError);
            }

            throw new InvalidOperationException(
                $"Nested macro '{name}' failed to compile:\n{result.CompilationError}");
        }
        finally
        {
            ActiveRunKeys.Value = previousSet;
        }
    }

    /// <summary>
    /// Inserts a code snippet by typing its <paramref name="prefix"/> at the caret and asking
    /// Visual Studio to expand it. Equivalent to typing the prefix and pressing
    /// <c>Tab</c> in the C# editor.
    /// </summary>
    /// <param name="prefix">
    /// The snippet shortcut (e.g. <c>"prop"</c>, <c>"for"</c>, <c>"cw"</c>) — must be non-empty.
    /// </param>
    /// <param name="cancellation">Token that aborts the operation between the type and expand steps.</param>
    /// <exception cref="ArgumentException"><paramref name="prefix"/> is null or whitespace.</exception>
    /// <exception cref="InvalidOperationException">No ambient <see cref="MacroGlobals"/> is set.</exception>
    /// <remarks>
    /// The caret must be in a snippet-aware editor surface (e.g. a C# document) for the
    /// expansion to fire; otherwise Visual Studio shows the snippet picker instead. This is a
    /// thin convenience over <see cref="TypeAsync"/> + <see cref="ExecuteCommandAsync"/> with
    /// <c>"Edit.InsertSnippet"</c>.
    /// </remarks>
    public static async Task InsertSnippetAsync(string prefix, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            throw new ArgumentException("Snippet prefix must be non-empty.", nameof(prefix));
        }

        await TypeAsync(prefix, cancellation).ConfigureAwait(false);
        await ExecuteCommandAsync("Edit.InsertSnippet", string.Empty, cancellation).ConfigureAwait(false);
    }

    private static async Task<(string? Source, MacroScope Scope)> ResolveMacroAsync(IMacroStore store, string name, CancellationToken cancellation)
    {
        // Try repo first to honour repo-wins. IMacroStore.LoadByNameAsync throws
        // InvalidOperationException for repo scope when no solution is open; treat that as
        // "not found in repo" rather than propagating it to the script.
        try
        {
            string? repoSrc = await store.LoadByNameAsync(name, MacroScope.Repo, cancellation).ConfigureAwait(false);
            if (repoSrc is not null)
            {
                return (repoSrc, MacroScope.Repo);
            }
        }
        catch (InvalidOperationException)
        {
            // No solution open — fall through to global lookup.
        }
        catch (ArgumentException)
        {
            // Invalid macro name (e.g. contains a path separator). Re-thrown by the global
            // attempt below; surface that single failure to the caller instead of silently
            // skipping it here.
        }

        string? globalSrc = await store.LoadByNameAsync(name, MacroScope.Global, cancellation).ConfigureAwait(false);
        return (globalSrc, MacroScope.Global);
    }

    private static MacroGlobals RequireGlobals()
        => CurrentGlobals.Value ?? throw new InvalidOperationException(
            "No ambient MacroGlobals is set. Helpers can only be invoked from inside a macro " +
            "invocation; the macro player must assign Helpers.CurrentGlobals.Value before " +
            "calling script.RunAsync.");

    private static TextSelection RequireTextSelection()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        DTE2 dte = RequireGlobals().DTE;
        return dte.ActiveDocument?.Selection as TextSelection
            ?? throw new InvalidOperationException(
                "The active document has no text selection (no document is active, or it is not a text editor).");
    }
}
