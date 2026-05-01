using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Macros.Engine.Storage;

/// <summary>
/// Scope-restricted <see cref="IMacroStore"/> that only operates on the per-solution repo
/// library (typically <c>&lt;solution&gt;\.vs\Macros\</c>). Off-scope reads are silent
/// no-ops, off-scope writes throw <see cref="InvalidOperationException"/>. The single-file
/// (<c>current.csx</c>) API throws unconditionally — that storage location is global by
/// definition and not reachable through this store.
/// </summary>
/// <remarks>
/// <para>
/// M4 splits the unified <see cref="FileSystemMacroStore"/> (which understood both
/// <see cref="MacroScope.Global"/> and <see cref="MacroScope.Repo"/>) into two scope-aware
/// stores so triggers, watchers, and the Composite router can reason about a single scope
/// at a time. <see cref="GlobalMacroStore"/> is the global half; <see cref="RepoMacroStore"/>
/// is the repo half; <c>CompositeMacroStore</c> (separate todo) assembles them.
/// </para>
/// <para>
/// The active solution is supplied by a <see cref="Func{T}"/> resolved on every named-macro
/// call. The provider returns the absolute path of the per-solution macros folder, or
/// <see langword="null"/> when no solution is open. Repo writes against a
/// <see langword="null"/> provider throw <see cref="InvalidOperationException"/>; repo reads
/// silently return empty / <see langword="null"/> so UI surfaces can render a
/// no-solution-open state without special casing.
/// </para>
/// <para>
/// Implementation is intentionally a thin wrapper around a repo-only-mode
/// <see cref="FileSystemMacroStore"/> so the atomic-write, per-name semaphore, and
/// FileSystemWatcher logic isn't duplicated. The wrapper enforces the scope contract:
/// </para>
/// <list type="bullet">
///   <item><description>
///     Writes against <see cref="MacroScope.Global"/> (Save / Rename) throw
///     <see cref="InvalidOperationException"/> — a write to the wrong scope is a caller bug
///     the Composite router would have caught.
///   </description></item>
///   <item><description>
///     Reads against <see cref="MacroScope.Global"/> (List / LoadByName / RefreshEntry) are
///     silent no-ops — empty list, <see langword="null"/>, <see langword="null"/>.
///   </description></item>
///   <item><description>
///     <see cref="GetMacroPath"/> against <see cref="MacroScope.Global"/> throws — there is
///     no on-disk path to compute in the repo folder for a global-scoped macro, so the
///     question is meaningless and almost certainly a caller bug.
///   </description></item>
///   <item><description>
///     The single-file M2 API (<see cref="SaveCurrentAsync"/>, <see cref="LoadCurrentAsync"/>,
///     <see cref="DeleteCurrentAsync"/>, <see cref="CurrentPath"/>) throws — there is no
///     <c>current.csx</c> in the repo scope. The recorder owns <c>current.csx</c> and lives
///     entirely in the global scope.
///   </description></item>
/// </list>
/// </remarks>
public sealed class RepoMacroStore : IMacroStore, IDisposable
{
    private readonly FileSystemMacroStore _inner;

    /// <summary>
    /// Initializes a new <see cref="RepoMacroStore"/> backed by
    /// <paramref name="repoFolderProvider"/>.
    /// </summary>
    /// <param name="repoFolderProvider">
    /// Accessor returning the absolute path of the per-solution repo macros folder
    /// (typically <c>&lt;solution&gt;\.vs\Macros</c>), or <see langword="null"/> when no
    /// solution is open. Invoked on every named-macro call so the active solution may
    /// change at runtime — wire this from <c>SolutionContextTracker</c> in the VSIX.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="repoFolderProvider"/> is null.</exception>
    public RepoMacroStore(Func<string?> repoFolderProvider)
    {
        if (repoFolderProvider is null)
        {
            throw new ArgumentNullException(nameof(repoFolderProvider));
        }

        _inner = new FileSystemMacroStore(repoFolderProvider);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Throws <see cref="InvalidOperationException"/> — <c>current.csx</c> is global-scoped
    /// only and not reachable through the repo store.
    /// </remarks>
    public string CurrentPath =>
        throw new InvalidOperationException("RepoMacroStore does not expose a current.csx path; use GlobalMacroStore.");

    /// <inheritdoc />
    public event EventHandler<MacroLibraryChangedEventArgs>? LibraryChanged
    {
        add { _inner.LibraryChanged += value; }
        remove { _inner.LibraryChanged -= value; }
    }

    // ─── M2 single-file API (global-only, throws) ─────────────────────────────────────

    /// <inheritdoc />
    public Task SaveCurrentAsync(string source, CancellationToken cancellation = default) =>
        throw new InvalidOperationException("RepoMacroStore does not handle current.csx; use GlobalMacroStore.");

    /// <inheritdoc />
    public Task<string?> LoadCurrentAsync(CancellationToken cancellation = default) =>
        throw new InvalidOperationException("RepoMacroStore does not handle current.csx; use GlobalMacroStore.");

    /// <inheritdoc />
    public Task<bool> DeleteCurrentAsync(CancellationToken cancellation = default) =>
        throw new InvalidOperationException("RepoMacroStore does not handle current.csx; use GlobalMacroStore.");

    // ─── Named-macro API (scope-aware) ────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<IReadOnlyList<MacroEntry>> ListAsync(MacroScope scope, CancellationToken cancellation = default)
    {
        if (scope != MacroScope.Repo)
        {
            return Task.FromResult<IReadOnlyList<MacroEntry>>(Array.Empty<MacroEntry>());
        }

        return _inner.ListAsync(MacroScope.Repo, cancellation);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MacroEntry>> ListAllAsync(CancellationToken cancellation = default)
        => _inner.ListAllAsync(cancellation);

    /// <inheritdoc />
    public Task<MacroEntry?> RefreshEntryAsync(string name, MacroScope scope, CancellationToken cancellation = default)
    {
        if (scope != MacroScope.Repo)
        {
            return Task.FromResult<MacroEntry?>(null);
        }

        return _inner.RefreshEntryAsync(name, MacroScope.Repo, cancellation);
    }

    /// <inheritdoc />
    public Task<string?> LoadByNameAsync(string name, MacroScope scope, CancellationToken cancellation = default)
    {
        if (scope != MacroScope.Repo)
        {
            return Task.FromResult<string?>(null);
        }

        return _inner.LoadByNameAsync(name, MacroScope.Repo, cancellation);
    }

    /// <inheritdoc />
    public Task SaveAsAsync(string name, string source, MacroScope scope, bool overwrite = false, CancellationToken cancellation = default)
    {
        if (scope != MacroScope.Repo)
        {
            throw new InvalidOperationException(
                $"RepoMacroStore only handles {nameof(MacroScope)}.{nameof(MacroScope.Repo)}; got {scope}.");
        }

        return _inner.SaveAsAsync(name, source, MacroScope.Repo, overwrite, cancellation);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string name, MacroScope scope, CancellationToken cancellation = default)
    {
        if (scope != MacroScope.Repo)
        {
            return Task.FromResult(false);
        }

        return _inner.DeleteAsync(name, MacroScope.Repo, cancellation);
    }

    /// <inheritdoc />
    public Task RenameAsync(string oldName, string newName, MacroScope scope, CancellationToken cancellation = default)
    {
        if (scope != MacroScope.Repo)
        {
            throw new InvalidOperationException(
                $"RepoMacroStore only handles {nameof(MacroScope)}.{nameof(MacroScope.Repo)}; got {scope}.");
        }

        return _inner.RenameAsync(oldName, newName, MacroScope.Repo, cancellation);
    }

    /// <inheritdoc />
    public string GetMacroPath(string name, MacroScope scope)
    {
        if (scope != MacroScope.Repo)
        {
            throw new InvalidOperationException(
                $"RepoMacroStore only handles {nameof(MacroScope)}.{nameof(MacroScope.Repo)}; got {scope}.");
        }

        return _inner.GetMacroPath(name, MacroScope.Repo);
    }

    /// <inheritdoc />
    public bool IsValidName(string name) => _inner.IsValidName(name);

    /// <summary>
    /// Disposes the underlying <see cref="FileSystemMacroStore"/>, which tears down the
    /// repo file-system watcher and releases its semaphores.
    /// </summary>
    public void Dispose() => _inner.Dispose();
}
