using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Macros.Engine.Storage;

/// <summary>
/// Scope-restricted <see cref="IMacroStore"/> that only operates on the global library
/// (typically <c>%APPDATA%\Macros\Macros\</c>). Off-scope reads are silent no-ops, off-scope
/// writes throw <see cref="InvalidOperationException"/>.
/// </summary>
/// <remarks>
/// <para>
/// M4 splits the unified <see cref="FileSystemMacroStore"/> (which understood both
/// <see cref="MacroScope.Global"/> and <see cref="MacroScope.Repo"/>) into two scope-aware
/// stores so triggers, watchers, and the Composite router can reason about a single scope
/// at a time. <see cref="GlobalMacroStore"/> is the global half; <c>RepoMacroStore</c>
/// (separate todo) is the repo half; <c>CompositeMacroStore</c> assembles them.
/// </para>
/// <para>
/// Implementation is intentionally a thin wrapper around <see cref="FileSystemMacroStore"/>
/// with <c>repoFolderProvider: null</c> so the global named-folder + atomic-write +
/// watcher logic isn't duplicated. The wrapper enforces the scope contract:
/// </para>
/// <list type="bullet">
///   <item><description>
///     Writes against <see cref="MacroScope.Repo"/> (Save / Delete / Rename) throw
///     <see cref="InvalidOperationException"/> — a write to the wrong scope is a caller bug
///     the Composite router would have caught.
///   </description></item>
///   <item><description>
///     Reads against <see cref="MacroScope.Repo"/> (List / LoadByName / RefreshEntry) are
///     silent no-ops — empty list, <see langword="null"/>, <see langword="null"/>. This
///     matches the intuition that "this store has nothing in the repo scope" and lets the
///     Composite cleanly union enumerations from both halves.
///   </description></item>
///   <item><description>
///     <see cref="GetMacroPath"/> against <see cref="MacroScope.Repo"/> throws — there is
///     no on-disk path to compute in the global folder for a repo-scoped macro, so the
///     question is meaningless and almost certainly a caller bug.
///   </description></item>
/// </list>
/// <para>
/// The single-file M2 API (<see cref="SaveCurrentAsync"/>, <see cref="LoadCurrentAsync"/>,
/// <see cref="DeleteCurrentAsync"/>, <see cref="CurrentPath"/>) is scope-less — it lives
/// in the global folder by construction — and passes straight through.
/// </para>
/// </remarks>
public sealed class GlobalMacroStore : IMacroStore, IDisposable
{
    private readonly FileSystemMacroStore _inner;

    /// <summary>
    /// Initializes a new <see cref="GlobalMacroStore"/> rooted at <paramref name="globalFolder"/>.
    /// </summary>
    /// <param name="globalFolder">
    /// Absolute path of the global macros folder. Typically the result of
    /// <see cref="MacrosPaths.ResolveGlobalFolder(string?)"/>.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="globalFolder"/> is null or whitespace.</exception>
    public GlobalMacroStore(string globalFolder)
    {
        _inner = new FileSystemMacroStore(globalFolder, repoFolderProvider: null);
    }

    /// <inheritdoc />
    public string CurrentPath => _inner.CurrentPath;

    /// <inheritdoc />
    public event EventHandler<MacroLibraryChangedEventArgs>? LibraryChanged
    {
        add { _inner.LibraryChanged += value; }
        remove { _inner.LibraryChanged -= value; }
    }

    // ─── M2 single-file API (scope-less, passes through) ──────────────────────────────

    /// <inheritdoc />
    public Task SaveCurrentAsync(string source, CancellationToken cancellation = default)
        => _inner.SaveCurrentAsync(source, cancellation);

    /// <inheritdoc />
    public Task<string?> LoadCurrentAsync(CancellationToken cancellation = default)
        => _inner.LoadCurrentAsync(cancellation);

    /// <inheritdoc />
    public Task<bool> DeleteCurrentAsync(CancellationToken cancellation = default)
        => _inner.DeleteCurrentAsync(cancellation);

    // ─── Named-macro API (scope-aware) ────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<IReadOnlyList<MacroEntry>> ListAsync(MacroScope scope, CancellationToken cancellation = default)
    {
        if (scope != MacroScope.Global)
        {
            return Task.FromResult<IReadOnlyList<MacroEntry>>(Array.Empty<MacroEntry>());
        }

        return _inner.ListAsync(MacroScope.Global, cancellation);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MacroEntry>> ListAllAsync(CancellationToken cancellation = default)
        => _inner.ListAsync(MacroScope.Global, cancellation);

    /// <inheritdoc />
    public Task<MacroEntry?> RefreshEntryAsync(string name, MacroScope scope, CancellationToken cancellation = default)
    {
        if (scope != MacroScope.Global)
        {
            return Task.FromResult<MacroEntry?>(null);
        }

        return _inner.RefreshEntryAsync(name, MacroScope.Global, cancellation);
    }

    /// <inheritdoc />
    public Task<string?> LoadByNameAsync(string name, MacroScope scope, CancellationToken cancellation = default)
    {
        if (scope != MacroScope.Global)
        {
            return Task.FromResult<string?>(null);
        }

        return _inner.LoadByNameAsync(name, MacroScope.Global, cancellation);
    }

    /// <inheritdoc />
    public Task SaveAsAsync(string name, string source, MacroScope scope, bool overwrite = false, CancellationToken cancellation = default)
    {
        if (scope != MacroScope.Global)
        {
            throw new InvalidOperationException(
                $"GlobalMacroStore only handles {nameof(MacroScope)}.{nameof(MacroScope.Global)}; got {scope}.");
        }

        return _inner.SaveAsAsync(name, source, MacroScope.Global, overwrite, cancellation);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string name, MacroScope scope, CancellationToken cancellation = default)
    {
        if (scope != MacroScope.Global)
        {
            return Task.FromResult(false);
        }

        return _inner.DeleteAsync(name, MacroScope.Global, cancellation);
    }

    /// <inheritdoc />
    public Task RenameAsync(string oldName, string newName, MacroScope scope, CancellationToken cancellation = default)
    {
        if (scope != MacroScope.Global)
        {
            throw new InvalidOperationException(
                $"GlobalMacroStore only handles {nameof(MacroScope)}.{nameof(MacroScope.Global)}; got {scope}.");
        }

        return _inner.RenameAsync(oldName, newName, MacroScope.Global, cancellation);
    }

    /// <inheritdoc />
    public string GetMacroPath(string name, MacroScope scope)
    {
        if (scope != MacroScope.Global)
        {
            throw new InvalidOperationException(
                $"GlobalMacroStore only handles {nameof(MacroScope)}.{nameof(MacroScope.Global)}; got {scope}.");
        }

        return _inner.GetMacroPath(name, MacroScope.Global);
    }

    /// <inheritdoc />
    public bool IsValidName(string name) => _inner.IsValidName(name);

    /// <summary>
    /// Disposes the underlying <see cref="FileSystemMacroStore"/>, which tears down the
    /// file-system watcher and releases its semaphores.
    /// </summary>
    public void Dispose() => _inner.Dispose();
}
