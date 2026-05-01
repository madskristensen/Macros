using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Macros.Engine.Storage;

/// <summary>
/// Persistent storage for macro source code. The M2 surface only knows about a single
/// "current" macro — the most-recently-recorded ad-hoc macro that <c>Play Last</c>
/// re-executes. Named macros, the repo macro library, and sync arrive in M3.
/// </summary>
/// <remarks>
/// Implementations must be safe to call from any thread; the engine fires saves as
/// fire-and-forget after a recording stops, so blocking the caller on disk I/O is
/// explicitly disallowed. Saves should be atomic — a crash midway through a write must
/// either leave the previous <c>current.csx</c> intact or produce the new one fully.
/// </remarks>
public interface IMacroStorage
{
    /// <summary>
    /// Persists <paramref name="source"/> as the current macro. Overwrites any existing
    /// <c>current.csx</c>. Creates the storage folder if it does not yet exist.
    /// </summary>
    /// <param name="source">The full <c>.csx</c> source to write.</param>
    /// <param name="cancellation">Cancels the save before the file is written.</param>
    Task SaveCurrentAsync(string source, CancellationToken cancellation = default);

    /// <summary>
    /// Reads the persisted current macro source, or <see langword="null"/> if no
    /// <c>current.csx</c> exists (first run, deleted, or never recorded).
    /// </summary>
    /// <param name="cancellation">Cancels the read before the file is opened.</param>
    Task<string?> LoadCurrentAsync(CancellationToken cancellation = default);

    /// <summary>
    /// Removes the persisted current macro. Returns <see langword="true"/> when a file
    /// was deleted, <see langword="false"/> when no file existed (idempotent no-op).
    /// </summary>
    /// <param name="cancellation">Cancels the delete before the file is removed.</param>
    Task<bool> DeleteCurrentAsync(CancellationToken cancellation = default);

    /// <summary>
    /// Gets the absolute path that <see cref="SaveCurrentAsync"/>, <see cref="LoadCurrentAsync"/>,
    /// and <see cref="DeleteCurrentAsync"/> operate on. Useful for the future "Edit current macro"
    /// command which needs to open this file in the VS editor.
    /// </summary>
    string CurrentPath { get; }

    // ─── Named-macro library API (M3) ──────────────────────────────────────────────────
    //
    // The M3 surface adds a multi-file macro library on top of the single-file M2 surface.
    // Files live in two scope folders (see MacroScope) and have a stable .csx name. The
    // M2 single-file `current.csx` is reserved for the most-recently-recorded ad-hoc
    // macro and is intentionally excluded from the named-macro listing — Save As is what
    // promotes it into the library.

    /// <summary>
    /// Enumerates every named macro in the requested <paramref name="scope"/>. Returns an
    /// empty list when the scope folder doesn't exist yet (first run, fresh repo). The
    /// reserved <c>current.csx</c> from the M2 single-file API is filtered out. Results
    /// are sorted by name ascending so the tool window can render deterministically.
    /// </summary>
    /// <param name="scope">Which scope folder to enumerate.</param>
    /// <param name="cancellation">Cancels the enumeration before any I/O.</param>
    /// <exception cref="System.InvalidOperationException">
    /// <paramref name="scope"/> is <see cref="MacroScope.Repo"/> and no solution is open.
    /// </exception>
    Task<IReadOnlyList<MacroDescriptor>> ListAsync(MacroScope scope, CancellationToken cancellation = default);

    /// <summary>
    /// Convenience wrapper that returns the union of <see cref="ListAsync"/> for every
    /// scope. The repo scope is silently skipped when no solution is open so the tool
    /// window can call this from a no-solution startup without special-casing.
    /// </summary>
    /// <param name="cancellation">Cancels the enumeration before any I/O.</param>
    Task<IReadOnlyList<MacroDescriptor>> ListAllAsync(CancellationToken cancellation = default);

    /// <summary>
    /// Loads the source of the named macro from <paramref name="scope"/>, or
    /// <see langword="null"/> when no file with that name exists.
    /// </summary>
    /// <param name="name">The macro name (file stem, no extension). Validated via <see cref="IsValidName"/>.</param>
    /// <param name="scope">Which scope folder to look in.</param>
    /// <param name="cancellation">Cancels the read before the file is opened.</param>
    /// <exception cref="System.ArgumentException"><paramref name="name"/> fails <see cref="IsValidName"/>.</exception>
    /// <exception cref="System.InvalidOperationException">
    /// <paramref name="scope"/> is <see cref="MacroScope.Repo"/> and no solution is open.
    /// </exception>
    Task<string?> LoadByNameAsync(string name, MacroScope scope, CancellationToken cancellation = default);

    /// <summary>
    /// Persists <paramref name="source"/> as <c><paramref name="name"/>.csx</c> in
    /// <paramref name="scope"/>. The save is atomic (temp+swap) so a crash midway through
    /// leaves the previous content intact. Raises <see cref="LibraryChanged"/> with
    /// <see cref="MacroLibraryChangeKind.Added"/> when the file is new, or
    /// <see cref="MacroLibraryChangeKind.Modified"/> when an existing file was overwritten.
    /// </summary>
    /// <param name="name">The macro name (file stem, no extension). Validated via <see cref="IsValidName"/>.</param>
    /// <param name="source">The full <c>.csx</c> source to write.</param>
    /// <param name="scope">Which scope folder to write to.</param>
    /// <param name="overwrite">
    /// When <see langword="false"/> (the default) and a file with that name already exists,
    /// throws <see cref="System.InvalidOperationException"/>. When <see langword="true"/>,
    /// the existing file is replaced atomically.
    /// </param>
    /// <param name="cancellation">Cancels the save before the file is written.</param>
    /// <exception cref="System.ArgumentException"><paramref name="name"/> fails <see cref="IsValidName"/>.</exception>
    /// <exception cref="System.ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.InvalidOperationException">
    /// <paramref name="scope"/> is <see cref="MacroScope.Repo"/> and no solution is open, OR
    /// the macro already exists and <paramref name="overwrite"/> is <see langword="false"/>.
    /// </exception>
    Task SaveAsAsync(string name, string source, MacroScope scope, bool overwrite = false, CancellationToken cancellation = default);

    /// <summary>
    /// Removes the named macro from <paramref name="scope"/>. Returns <see langword="false"/>
    /// when no file with that name exists (idempotent no-op, no event raised);
    /// <see langword="true"/> on success and raises <see cref="LibraryChanged"/> with
    /// <see cref="MacroLibraryChangeKind.Removed"/>.
    /// </summary>
    /// <param name="name">The macro name (file stem, no extension). Validated via <see cref="IsValidName"/>.</param>
    /// <param name="scope">Which scope folder to delete from.</param>
    /// <param name="cancellation">Cancels the delete before the file is removed.</param>
    /// <exception cref="System.ArgumentException"><paramref name="name"/> fails <see cref="IsValidName"/>.</exception>
    /// <exception cref="System.InvalidOperationException">
    /// <paramref name="scope"/> is <see cref="MacroScope.Repo"/> and no solution is open.
    /// </exception>
    Task<bool> DeleteAsync(string name, MacroScope scope, CancellationToken cancellation = default);

    /// <summary>
    /// Renames an existing macro from <paramref name="oldName"/> to <paramref name="newName"/>
    /// in <paramref name="scope"/>. The move is atomic via <see cref="System.IO.File.Move(string, string)"/>.
    /// Raises <see cref="LibraryChanged"/> with <see cref="MacroLibraryChangeKind.Renamed"/>
    /// and <see cref="MacroLibraryChangedEventArgs.OldName"/> populated.
    /// </summary>
    /// <param name="oldName">Current name of the macro. Must exist.</param>
    /// <param name="newName">New name. Must not collide with an existing macro in <paramref name="scope"/>.</param>
    /// <param name="scope">Which scope folder the macro lives in (rename never crosses scopes).</param>
    /// <param name="cancellation">Cancels the rename before the file system call.</param>
    /// <exception cref="System.ArgumentException">Either name fails <see cref="IsValidName"/>.</exception>
    /// <exception cref="System.InvalidOperationException">
    /// <paramref name="scope"/> is <see cref="MacroScope.Repo"/> and no solution is open,
    /// the source macro doesn't exist, or the destination name is already taken.
    /// </exception>
    Task RenameAsync(string oldName, string newName, MacroScope scope, CancellationToken cancellation = default);

    /// <summary>
    /// Returns the absolute path the storage uses for <paramref name="name"/> in
    /// <paramref name="scope"/>. Pure path computation — does not touch the file system.
    /// Useful for "Open in editor" commands that route through <c>VS.Documents</c>.
    /// </summary>
    /// <param name="name">The macro name (file stem, no extension). Validated via <see cref="IsValidName"/>.</param>
    /// <param name="scope">Which scope folder to compute the path under.</param>
    /// <exception cref="System.ArgumentException"><paramref name="name"/> fails <see cref="IsValidName"/>.</exception>
    /// <exception cref="System.InvalidOperationException">
    /// <paramref name="scope"/> is <see cref="MacroScope.Repo"/> and no solution is open.
    /// </exception>
    string GetMacroPath(string name, MacroScope scope);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="name"/> satisfies every macro
    /// naming rule (length, character set, reserved names, hidden-file prefix, M2
    /// <c>current</c> reservation). The rules are documented on the implementation.
    /// </summary>
    bool IsValidName(string name);

    /// <summary>
    /// Raised when the macro library changes — both as a result of this storage's own
    /// Save / Delete / Rename operations and (in the next wave) when an external editor
    /// mutates a file the watcher is observing.
    /// </summary>
    /// <remarks>
    /// Fired <strong>synchronously on whichever thread performed the change</strong>.
    /// Subscribers (the storage watcher, the tool window) are responsible for marshalling
    /// to the UI thread before touching WPF state.
    /// </remarks>
    event EventHandler<MacroLibraryChangedEventArgs>? LibraryChanged;
}
