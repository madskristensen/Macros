using System;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
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

        dte.ItemOperations.OpenFile(path);
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
