using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Macros.Engine.Storage;

/// <summary>
/// Routing <see cref="IMacroStore"/> that fronts a global half (<see cref="GlobalMacroStore"/>)
/// and a repo half (<see cref="RepoMacroStore"/>) and dispatches every operation to whichever
/// child owns the requested <see cref="MacroScope"/>. The single-file (<c>current.csx</c>) API
/// is global by definition and always passes through to the global child.
/// </summary>
/// <remarks>
/// <para>
/// M4 splits the unified <see cref="FileSystemMacroStore"/> into two scope-aware halves so
/// triggers, watchers, and the storage router can each reason about a single scope at a
/// time. <see cref="CompositeMacroStore"/> is the router: every consumer (the engine, the
/// tool window, trigger registries) keeps holding a single <see cref="IMacroStore"/> and
/// the composite picks the right child based on the <see cref="MacroScope"/> parameter the
/// caller already supplies.
/// </para>
/// <para>
/// Routing rules:
/// </para>
/// <list type="bullet">
///   <item><description>
///     Single-file API (<see cref="SaveCurrentAsync"/>, <see cref="LoadCurrentAsync"/>,
///     <see cref="DeleteCurrentAsync"/>, <see cref="CurrentPath"/>) → global child.
///     <c>current.csx</c> is the most-recently-recorded ad-hoc macro and lives in the
///     global folder by construction.
///   </description></item>
///   <item><description>
///     Named-macro API with <see cref="MacroScope.Global"/> → global child.
///   </description></item>
///   <item><description>
///     Named-macro API with <see cref="MacroScope.Repo"/> → repo child.
///   </description></item>
///   <item><description>
///     <see cref="ListAllAsync"/> unions both children with a <strong>repo-wins</strong>
///     collision policy: when both halves contain a macro with the same name, the repo
///     entry replaces the global entry. The intuition is that a repo macro is closer to
///     the user's working project and therefore the more contextually relevant
///     definition. The repo half is silently skipped (treated as empty) when no solution
///     is open so the tool window can call <see cref="ListAllAsync"/> on a no-solution
///     startup without special-casing.
///   </description></item>
///   <item><description>
///     <see cref="LibraryChanged"/> is aggregated: the composite re-raises every event
///     either child fires, on the same thread the child fired it on. The originating
///     child is identified by <see cref="MacroLibraryChangedEventArgs.Scope"/>.
///   </description></item>
/// </list>
/// <para>
/// The composite is a pure router; it owns no file-system state of its own. Construction
/// just attaches the aggregator to each child's <see cref="LibraryChanged"/> event.
/// Disposal detaches the same handlers and — when <c>ownsChildren</c> is <see langword="true"/>
/// (the default) — disposes both children. Pass <c>ownsChildren: false</c> when the caller
/// already holds and disposes the children separately (e.g. test fixtures).
/// </para>
/// </remarks>
public sealed class CompositeMacroStore : IMacroStore, IDisposable
{
    private readonly IMacroStore _global;
    private readonly IMacroStore _repo;
    private readonly bool _ownsChildren;
    private EventHandler<MacroLibraryChangedEventArgs>? _libraryChanged;

    /// <summary>
    /// Initializes a new <see cref="CompositeMacroStore"/> that routes between
    /// <paramref name="global"/> and <paramref name="repo"/>.
    /// </summary>
    /// <param name="global">
    /// The global half — typically a <see cref="GlobalMacroStore"/>. Receives every
    /// single-file operation and every named-macro operation tagged
    /// <see cref="MacroScope.Global"/>.
    /// </param>
    /// <param name="repo">
    /// The repo half — typically a <see cref="RepoMacroStore"/>. Receives every named-macro
    /// operation tagged <see cref="MacroScope.Repo"/>.
    /// </param>
    /// <param name="ownsChildren">
    /// When <see langword="true"/> (the default), <see cref="Dispose"/> also disposes both
    /// children if they implement <see cref="IDisposable"/>. When <see langword="false"/>
    /// the caller retains ownership and must dispose them separately — useful when the
    /// children are shared with another component or constructed by a DI container.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="global"/> or <paramref name="repo"/> is <see langword="null"/>.
    /// </exception>
    public CompositeMacroStore(IMacroStore global, IMacroStore repo, bool ownsChildren = true)
    {
        _global = global ?? throw new ArgumentNullException(nameof(global));
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _ownsChildren = ownsChildren;
        _global.LibraryChanged += OnChildLibraryChanged;
        _repo.LibraryChanged += OnChildLibraryChanged;
    }

    /// <inheritdoc />
    public string CurrentPath => _global.CurrentPath;

    /// <inheritdoc />
    public event EventHandler<MacroLibraryChangedEventArgs>? LibraryChanged
    {
        add { _libraryChanged += value; }
        remove { _libraryChanged -= value; }
    }

    // ─── M2 single-file API (global by definition) ────────────────────────────────────

    /// <inheritdoc />
    public Task SaveCurrentAsync(string source, CancellationToken cancellation = default)
        => _global.SaveCurrentAsync(source, cancellation);

    /// <inheritdoc />
    public Task<string?> LoadCurrentAsync(CancellationToken cancellation = default)
        => _global.LoadCurrentAsync(cancellation);

    /// <inheritdoc />
    public Task<bool> DeleteCurrentAsync(CancellationToken cancellation = default)
        => _global.DeleteCurrentAsync(cancellation);

    /// <inheritdoc />
    public bool IsValidName(string name) => _global.IsValidName(name);

    // ─── Named-macro API (scope-routed) ───────────────────────────────────────────────

    /// <inheritdoc />
    public Task<IReadOnlyList<MacroEntry>> ListAsync(MacroScope scope, CancellationToken cancellation = default)
        => scope switch
        {
            MacroScope.Global => _global.ListAsync(scope, cancellation),
            MacroScope.Repo => _repo.ListAsync(scope, cancellation),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown macro scope."),
        };

    /// <inheritdoc />
    /// <remarks>
    /// Unions both halves with a repo-wins collision policy (see class remarks). The repo
    /// half is silently treated as empty when <see cref="RepoMacroStore"/> throws
    /// <see cref="InvalidOperationException"/> because no solution is open — this matches
    /// the IMacroStore.ListAllAsync contract that the no-solution case must not surface as
    /// an error to the tool window.
    /// </remarks>
    public async Task<IReadOnlyList<MacroEntry>> ListAllAsync(CancellationToken cancellation = default)
    {
        var globalTask = _global.ListAsync(MacroScope.Global, cancellation);

        IReadOnlyList<MacroEntry> repoList;
        try
        {
            repoList = await _repo.ListAsync(MacroScope.Repo, cancellation).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // No solution open — repo half intentionally degrades to empty so the tool
            // window can render a global-only list without surfacing an error.
            repoList = Array.Empty<MacroEntry>();
        }

        var globalList = await globalTask.ConfigureAwait(false);
        return MergeWithRepoWins(globalList, repoList);
    }

    /// <inheritdoc />
    public Task<MacroEntry?> RefreshEntryAsync(string name, MacroScope scope, CancellationToken cancellation = default)
        => scope switch
        {
            MacroScope.Global => _global.RefreshEntryAsync(name, scope, cancellation),
            MacroScope.Repo => _repo.RefreshEntryAsync(name, scope, cancellation),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown macro scope."),
        };

    /// <inheritdoc />
    public Task<string?> LoadByNameAsync(string name, MacroScope scope, CancellationToken cancellation = default)
        => scope switch
        {
            MacroScope.Global => _global.LoadByNameAsync(name, scope, cancellation),
            MacroScope.Repo => _repo.LoadByNameAsync(name, scope, cancellation),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown macro scope."),
        };

    /// <inheritdoc />
    public Task SaveAsAsync(string name, string source, MacroScope scope, bool overwrite = false, CancellationToken cancellation = default)
        => scope switch
        {
            MacroScope.Global => _global.SaveAsAsync(name, source, scope, overwrite, cancellation),
            MacroScope.Repo => _repo.SaveAsAsync(name, source, scope, overwrite, cancellation),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown macro scope."),
        };

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string name, MacroScope scope, CancellationToken cancellation = default)
        => scope switch
        {
            MacroScope.Global => _global.DeleteAsync(name, scope, cancellation),
            MacroScope.Repo => _repo.DeleteAsync(name, scope, cancellation),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown macro scope."),
        };

    /// <inheritdoc />
    public Task RenameAsync(string oldName, string newName, MacroScope scope, CancellationToken cancellation = default)
        => scope switch
        {
            MacroScope.Global => _global.RenameAsync(oldName, newName, scope, cancellation),
            MacroScope.Repo => _repo.RenameAsync(oldName, newName, scope, cancellation),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown macro scope."),
        };

    /// <inheritdoc />
    public string GetMacroPath(string name, MacroScope scope)
        => scope switch
        {
            MacroScope.Global => _global.GetMacroPath(name, scope),
            MacroScope.Repo => _repo.GetMacroPath(name, scope),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown macro scope."),
        };

    /// <summary>
    /// Detaches the aggregator from both children's <see cref="IMacroStore.LibraryChanged"/>
    /// events. When constructed with <c>ownsChildren: true</c>, also disposes both children.
    /// </summary>
    public void Dispose()
    {
        _global.LibraryChanged -= OnChildLibraryChanged;
        _repo.LibraryChanged -= OnChildLibraryChanged;

        if (_ownsChildren)
        {
            (_global as IDisposable)?.Dispose();
            (_repo as IDisposable)?.Dispose();
        }
    }

    private void OnChildLibraryChanged(object? sender, MacroLibraryChangedEventArgs e)
    {
        // Re-raise on the same thread the child fired on; the composite is a pure router
        // and must not impose threading semantics the underlying store didn't promise.
        _libraryChanged?.Invoke(this, e);
    }

    private static IReadOnlyList<MacroEntry> MergeWithRepoWins(
        IReadOnlyList<MacroEntry> global,
        IReadOnlyList<MacroEntry> repo)
    {
        var byName = new Dictionary<string, MacroEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in global)
        {
            byName[entry.Name] = entry;
        }

        foreach (var entry in repo)
        {
            // Repo-wins: a same-name entry from the repo half overwrites the global one.
            // Closer to the user's working project ⇒ more locally relevant definition.
            byName[entry.Name] = entry;
        }

        return byName.Values
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
