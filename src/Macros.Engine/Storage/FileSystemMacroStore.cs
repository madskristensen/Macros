using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine.Triggers;

namespace Macros.Engine.Storage;

/// <summary>
/// Default <see cref="IMacroStore"/> backed by the local file system. Persists the
/// current ad-hoc macro as a single <c>current.csx</c> file under the configured global
/// folder, and the named-macro library as <c>&lt;name&gt;.csx</c> files under either the
/// global <c>Macros\</c> subfolder or the per-solution <c>.vs\Macros\</c> folder.
/// </summary>
/// <remarks>
/// <para>
/// Saves are atomic: the new content is written to a unique sibling <c>.tmp</c> file
/// first, then swapped into place via <see cref="File.Replace(string, string, string)"/>
/// when an existing file is being overwritten, or <see cref="File.Move(string, string)"/>
/// when the destination does not yet exist. A crash midway through a write therefore
/// leaves the previous file intact and at worst leaves a stray <c>.tmp</c> file behind,
/// which the next save overwrites.
/// </para>
/// <para>
/// Concurrency: the legacy single-file API funnels its swap step through a single
/// <see cref="SemaphoreSlim"/>; the named-macro API takes a per-name semaphore so writes
/// to different files parallelize while writes to the same name serialize cleanly.
/// </para>
/// <para>
/// All I/O is dispatched onto the thread pool via <see cref="Task.Run(System.Action)"/>
/// so fire-and-forget save sites in <see cref="MacroService"/> never block the caller.
/// Files are written as UTF-8 with BOM so the Visual Studio editor opens them with the
/// correct encoding when the user invokes "Edit current macro" or opens a named macro.
/// </para>
/// <para>
/// The repo scope is solution-relative: a <see cref="Func{T}"/> supplied at construction
/// time is invoked on every named-macro call, so the active solution can change at
/// runtime (open / close / switch) without rebuilding the storage. When the provider
/// returns <see langword="null"/> (no solution open), every named-macro call against
/// <see cref="MacroScope.Repo"/> throws <see cref="InvalidOperationException"/>; UI
/// surfaces should hide repo-scoped commands while no solution is open.
/// </para>
/// </remarks>
public sealed class FileSystemMacroStore : IMacroStore, IDisposable
{
    private const string CurrentFileName = "current.csx";
    private const string CurrentReservedName = "current";
    private const string MacroExtension = ".csx";
    private const string GlobalNamedSubfolder = "Macros";
    private const int MaxNameLength = 60;

    // Cap the on-disk read in ParseHeaderAsync so a multi-megabyte .csx (which would not
    // be a recorded macro anyway) doesn't blow memory while enumerating a large library.
    private const int HeaderReadBudgetBytes = 4 * 1024;

    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    // The Windows reserved device names. Compared case-insensitively. Stored sorted just
    // for readability — the lookup is a hash set so order doesn't matter.
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly string _globalFolder;
    private readonly string _globalNamedFolder;
    private readonly string _currentPath;
    private readonly Func<string?>? _repoFolderProvider;

    // When false, the global folder + current.csx wiring is unconfigured: every Global
    // and single-file (current.csx) operation throws InvalidOperationException, and the
    // global FileSystemWatcher is never started. This is the mode used by the wrapping
    // RepoMacroStore, which only ever delegates Repo-scoped calls to the inner instance.
    private readonly bool _globalEnabled;

    // Serializes the swap-into-place step for SaveCurrentAsync so concurrent saves can't
    // race File.Replace vs File.Move on the same destination. Writers still produce
    // unique tmp files so the critical section is only the swap.
    private readonly SemaphoreSlim _currentWriteLock = new(initialCount: 1, maxCount: 1);

    // Per-name semaphores for the named-macro API. The double-checked GetOrAdd pattern
    // keeps allocation off the hot path while letting writes to different files
    // parallelize.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _namedLocks =
        new(StringComparer.OrdinalIgnoreCase);

    // ─── FileSystemWatcher fields ──────────────────────────────────────────────────────

    private FileSystemWatcher? _globalWatcher;
    private FileSystemWatcher? _repoWatcher;
    private readonly object _watcherSync = new();

    // Paths written by this storage instance's own Save/Delete/Rename — used to suppress
    // the corresponding watcher event so we don't double-fire LibraryChanged.
    private readonly ConcurrentDictionary<string, byte> _recentSelfWrites =
        new(StringComparer.OrdinalIgnoreCase);

    // Debouncer: coalesces rapid FS events on the same path within a 200ms window.
    private System.Timers.Timer? _debounceTimer;
    private readonly ConcurrentDictionary<string, PendingChange> _pending =
        new(StringComparer.OrdinalIgnoreCase);

    private EventHandler<MacroLibraryChangedEventArgs>? _libraryChanged;

    private sealed record PendingChange(MacroLibraryChangeKind Kind, MacroScope Scope, string Name, string? OldName);

    /// <summary>
    /// Initializes a new <see cref="FileSystemMacroStore"/> rooted at
    /// <paramref name="globalFolder"/>. The folder is not created until the first save —
    /// constructing the storage is side-effect free so package initialization stays cheap.
    /// </summary>
    /// <param name="globalFolder">
    /// Absolute path to the macros root. Callers should resolve this via
    /// <see cref="MacrosPaths.ResolveGlobalFolder(string?)"/> so an unset option falls
    /// back to <c>%APPDATA%\Macros</c>. The legacy <c>current.csx</c> lives directly in
    /// this folder; named global macros live in the <c>Macros\</c> subfolder.
    /// </param>
    /// <param name="repoFolderProvider">
    /// Optional accessor returning the absolute path to the per-solution macros folder
    /// (typically <c>&lt;solution&gt;\.vs\Macros</c>), or <see langword="null"/> when no
    /// solution is open. Invoked on every named-macro call so the active solution may
    /// change at runtime. When omitted entirely, repo-scoped calls throw
    /// <see cref="InvalidOperationException"/>; this is the right default for unit tests
    /// that only exercise the global scope.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="globalFolder"/> is null or whitespace.</exception>
    public FileSystemMacroStore(string globalFolder, Func<string?>? repoFolderProvider = null)
    {
        if (string.IsNullOrWhiteSpace(globalFolder))
        {
            throw new ArgumentException("Global macros folder must be a non-empty path.", nameof(globalFolder));
        }

        _globalFolder = globalFolder;
        _globalNamedFolder = Path.Combine(globalFolder, GlobalNamedSubfolder);
        _currentPath = Path.Combine(globalFolder, CurrentFileName);
        _repoFolderProvider = repoFolderProvider;
        _globalEnabled = true;
    }

    /// <summary>
    /// Initializes a repo-only <see cref="FileSystemMacroStore"/>. Every call against
    /// <see cref="MacroScope.Global"/> and every single-file (current.csx) API throws
    /// <see cref="InvalidOperationException"/>; only the per-solution repo folder served
    /// by <paramref name="repoFolderProvider"/> is reachable. The global FileSystemWatcher
    /// is never started, which keeps <see cref="LibraryChanged"/> echoes scoped to the
    /// repo half.
    /// </summary>
    /// <remarks>
    /// This overload exists to back <c>RepoMacroStore</c>: the M4 split into scope-aware
    /// stores routes Global writes/reads through <c>GlobalMacroStore</c> and Repo
    /// writes/reads through this repo-only mode, with <c>CompositeMacroStore</c> joining
    /// the two halves. Marked <see langword="internal"/> because the public surface for
    /// repo-only construction is <c>RepoMacroStore</c>.
    /// </remarks>
    /// <param name="repoFolderProvider">
    /// Accessor returning the absolute path to the per-solution macros folder, or
    /// <see langword="null"/> when no solution is open. Invoked on every named-macro
    /// call so the active solution may change at runtime. When the provider returns
    /// <see langword="null"/>, repo-scoped operations throw
    /// <see cref="InvalidOperationException"/>.
    /// <para>
    /// NOTE: The <see cref="FileSystemWatcher"/> for the repo folder binds to the path
    /// returned by the <em>first</em> invocation of this provider (see
    /// <c>EnsureWatchersStarted</c>). If the provider returns a different path later
    /// (e.g., user switches solutions), the watcher does <em>not</em> restart —
    /// named-macro operations route correctly but external file changes in the new path
    /// won't raise <see cref="LibraryChanged"/>. Track via
    /// <c>m5-watcher-restart-on-solution-change</c>.
    /// </para>
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="repoFolderProvider"/> is null.</exception>
    internal FileSystemMacroStore(Func<string?> repoFolderProvider)
    {
        _repoFolderProvider = repoFolderProvider ?? throw new ArgumentNullException(nameof(repoFolderProvider));

        // Sentinels — every code path that would touch them is gated by _globalEnabled.
        _globalFolder = string.Empty;
        _globalNamedFolder = string.Empty;
        _currentPath = string.Empty;
        _globalEnabled = false;
    }

    /// <inheritdoc />
    public string CurrentPath
    {
        get
        {
            EnsureGlobalEnabled();
            return _currentPath;
        }
    }

    /// <inheritdoc />
    public event EventHandler<MacroLibraryChangedEventArgs>? LibraryChanged
    {
        add
        {
            _libraryChanged += value;
            EnsureWatchersStarted();
        }
        remove { _libraryChanged -= value; }
    }

    // ─── M2 single-file API ────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task SaveCurrentAsync(string source, CancellationToken cancellation = default)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        EnsureGlobalEnabled();
        cancellation.ThrowIfCancellationRequested();

        return Task.Run(async () =>
        {
            cancellation.ThrowIfCancellationRequested();

            Directory.CreateDirectory(_globalFolder);

            cancellation.ThrowIfCancellationRequested();

            var tempPath = _currentPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                File.WriteAllText(tempPath, source, Utf8WithBom);

                cancellation.ThrowIfCancellationRequested();

                await _currentWriteLock.WaitAsync(cancellation).ConfigureAwait(false);
                try
                {
                    SwapIntoPlace(tempPath, _currentPath);
                }
                finally
                {
                    _currentWriteLock.Release();
                }
            }
            catch
            {
                TryDeleteSilently(tempPath);
                throw;
            }
        }, cancellation);
    }

    /// <inheritdoc />
    public Task<string?> LoadCurrentAsync(CancellationToken cancellation = default)
    {
        EnsureGlobalEnabled();
        cancellation.ThrowIfCancellationRequested();

        return Task.Run<string?>(() =>
        {
            cancellation.ThrowIfCancellationRequested();

            if (!File.Exists(_currentPath))
            {
                return null;
            }

            cancellation.ThrowIfCancellationRequested();

            return File.ReadAllText(_currentPath, Utf8WithBom);
        }, cancellation);
    }

    /// <inheritdoc />
    public Task<bool> DeleteCurrentAsync(CancellationToken cancellation = default)
    {
        EnsureGlobalEnabled();
        cancellation.ThrowIfCancellationRequested();

        return Task.Run(() =>
        {
            cancellation.ThrowIfCancellationRequested();

            if (!File.Exists(_currentPath))
            {
                return false;
            }

            File.Delete(_currentPath);
            return true;
        }, cancellation);
    }

    // ─── M3+ named-macro API ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<IReadOnlyList<MacroEntry>> ListAsync(MacroScope scope, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();

        var folder = ResolveScopeFolder(scope);

        return Task.Run<IReadOnlyList<MacroEntry>>(() =>
        {
            cancellation.ThrowIfCancellationRequested();

            if (!Directory.Exists(folder))
            {
                return Array.Empty<MacroEntry>();
            }

            // Top-level only — nested folders are not part of the model. SearchOption.TopDirectoryOnly
            // is the default but spelling it out avoids surprises if the default ever changes.
            var files = Directory.EnumerateFiles(folder, "*" + MacroExtension, SearchOption.TopDirectoryOnly);
            var entries = new List<MacroEntry>();

            foreach (var path in files)
            {
                cancellation.ThrowIfCancellationRequested();

                var name = Path.GetFileNameWithoutExtension(path);

                // Filter the M2 reserved name regardless of scope. In the global named
                // subfolder this is impossible (the file lives one folder up) but a user
                // who manually drops a file called "current.csx" in either scope folder
                // shouldn't crash the listing.
                if (string.Equals(name, CurrentReservedName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var entry = TryBuildEntry(path, name, scope);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }

            entries.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            return entries;
        }, cancellation);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MacroEntry>> ListAllAsync(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();

        var combined = new List<MacroEntry>();

        // Global scope is optional in repo-only mode (RepoMacroStore wraps us). Skipping
        // it there keeps ListAllAsync usable as a "give me everything you have" query
        // without the caller having to special-case construction mode.
        if (_globalEnabled)
        {
            combined.AddRange(await ListAsync(MacroScope.Global, cancellation).ConfigureAwait(false));
        }

        // Repo scope is optional: silently skip if no solution is open so callers can
        // call this from a no-solution startup without special-casing.
        if (TryGetRepoFolder(out var _))
        {
            combined.AddRange(await ListAsync(MacroScope.Repo, cancellation).ConfigureAwait(false));
        }

        // Stable order across scopes so the tool window stays deterministic.
        combined.Sort(static (a, b) =>
        {
            var byScope = a.Scope.CompareTo(b.Scope);
            return byScope != 0 ? byScope : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        });

        return combined;
    }

    /// <inheritdoc />
    public Task<MacroEntry?> RefreshEntryAsync(string name, MacroScope scope, CancellationToken cancellation = default)
    {
        EnsureValidName(name);
        cancellation.ThrowIfCancellationRequested();

        var path = GetMacroPath(name, scope);

        return Task.Run<MacroEntry?>(() =>
        {
            cancellation.ThrowIfCancellationRequested();

            if (!File.Exists(path))
            {
                return null;
            }

            return TryBuildEntry(path, name, scope);
        }, cancellation);
    }

    /// <inheritdoc />
    public Task<string?> LoadByNameAsync(string name, MacroScope scope, CancellationToken cancellation = default)
    {
        EnsureValidName(name);
        cancellation.ThrowIfCancellationRequested();

        var path = GetMacroPath(name, scope);

        return Task.Run<string?>(() =>
        {
            cancellation.ThrowIfCancellationRequested();

            if (!File.Exists(path))
            {
                return null;
            }

            cancellation.ThrowIfCancellationRequested();
            return File.ReadAllText(path, Utf8WithBom);
        }, cancellation);
    }

    /// <inheritdoc />
    public Task SaveAsAsync(string name, string source, MacroScope scope, bool overwrite = false, CancellationToken cancellation = default)
    {
        EnsureValidName(name);
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        cancellation.ThrowIfCancellationRequested();

        var folder = ResolveScopeFolder(scope);
        var destination = Path.Combine(folder, name + MacroExtension);
        var perNameLock = GetNamedLock(scope, name);

        return Task.Run(async () =>
        {
            cancellation.ThrowIfCancellationRequested();

            await perNameLock.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                cancellation.ThrowIfCancellationRequested();

                Directory.CreateDirectory(folder);
                EnsureWatchersStarted();
                cancellation.ThrowIfCancellationRequested();

                var existed = File.Exists(destination);
                if (existed && !overwrite)
                {
                    throw new InvalidOperationException($"Macro already exists: {name}");
                }

                var tempPath = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(tempPath, source, Utf8WithBom);

                    cancellation.ThrowIfCancellationRequested();
                    RegisterSelfWrite(destination);
                    SwapIntoPlace(tempPath, destination);
                }
                catch
                {
                    TryDeleteSilently(tempPath);
                    throw;
                }

                RaiseLibraryChanged(new MacroLibraryChangedEventArgs(
                    existed ? MacroLibraryChangeKind.Modified : MacroLibraryChangeKind.Added,
                    scope,
                    name));
            }
            finally
            {
                perNameLock.Release();
            }
        }, cancellation);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string name, MacroScope scope, CancellationToken cancellation = default)
    {
        EnsureValidName(name);
        cancellation.ThrowIfCancellationRequested();

        var path = GetMacroPath(name, scope);
        var perNameLock = GetNamedLock(scope, name);

        return Task.Run(async () =>
        {
            cancellation.ThrowIfCancellationRequested();

            await perNameLock.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                if (!File.Exists(path))
                {
                    return false;
                }

                RegisterSelfWrite(path);
                File.Delete(path);
                RaiseLibraryChanged(new MacroLibraryChangedEventArgs(
                    MacroLibraryChangeKind.Removed, scope, name));
                return true;
            }
            finally
            {
                perNameLock.Release();
            }
        }, cancellation);
    }

    /// <inheritdoc />
    public Task RenameAsync(string oldName, string newName, MacroScope scope, CancellationToken cancellation = default)
    {
        EnsureValidName(oldName);
        EnsureValidName(newName);
        cancellation.ThrowIfCancellationRequested();

        var sourcePath = GetMacroPath(oldName, scope);
        var destinationPath = GetMacroPath(newName, scope);

        // Acquire both name locks to keep concurrent SaveAs/Delete on either name from
        // racing the move. Always acquire in alphabetical order to avoid deadlocks when
        // two renames cross over.
        var firstName = StringComparer.OrdinalIgnoreCase.Compare(oldName, newName) <= 0 ? oldName : newName;
        var secondName = ReferenceEquals(firstName, oldName) ? newName : oldName;
        var firstLock = GetNamedLock(scope, firstName);
        var secondLock = !string.Equals(firstName, secondName, StringComparison.OrdinalIgnoreCase)
            ? GetNamedLock(scope, secondName)
            : null;

        return Task.Run(async () =>
        {
            cancellation.ThrowIfCancellationRequested();

            await firstLock.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                if (secondLock is not null)
                {
                    await secondLock.WaitAsync(cancellation).ConfigureAwait(false);
                }

                try
                {
                    if (!File.Exists(sourcePath))
                    {
                        throw new InvalidOperationException($"Macro does not exist: {oldName}");
                    }

                    if (File.Exists(destinationPath) && !PathsEqualOnDisk(sourcePath, destinationPath))
                    {
                        throw new InvalidOperationException($"Macro already exists: {newName}");
                    }

                    RegisterSelfWrite(sourcePath);
                    RegisterSelfWrite(destinationPath);
                    File.Move(sourcePath, destinationPath);

                    RaiseLibraryChanged(new MacroLibraryChangedEventArgs(
                        MacroLibraryChangeKind.Renamed, scope, newName, oldName));
                }
                finally
                {
                    secondLock?.Release();
                }
            }
            finally
            {
                firstLock.Release();
            }
        }, cancellation);
    }

    /// <inheritdoc />
    public string GetMacroPath(string name, MacroScope scope)
    {
        EnsureValidName(name);
        var folder = ResolveScopeFolder(scope);
        return Path.Combine(folder, name + MacroExtension);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Naming rules enforced:
    /// <list type="bullet">
    ///   <item><description>1–60 characters in length.</description></item>
    ///   <item><description>Only letters, digits, hyphen, underscore, or single internal spaces.</description></item>
    ///   <item><description>No leading or trailing whitespace; no consecutive spaces.</description></item>
    ///   <item><description>None of the Windows path-illegal characters <c>&lt; &gt; : " / \ | ? *</c>.</description></item>
    ///   <item><description>No ASCII control characters (0–31).</description></item>
    ///   <item><description>Not a reserved Windows device name (CON, PRN, AUX, NUL, COM0–9, LPT0–9), case-insensitively.</description></item>
    ///   <item><description>Not the M2-reserved literal <c>"current"</c>, case-insensitively.</description></item>
    ///   <item><description>Does not start with a dot (would create a hidden file on POSIX file systems).</description></item>
    /// </list>
    /// </remarks>
    public bool IsValidName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (name.Length > MaxNameLength)
        {
            return false;
        }

        if (name[0] == '.')
        {
            return false;
        }

        if (name[0] == ' ' || name[name.Length - 1] == ' ')
        {
            return false;
        }

        for (int i = 0; i < name.Length; i++)
        {
            var c = name[i];

            if (c < 32)
            {
                return false;
            }

            switch (c)
            {
                case '<':
                case '>':
                case ':':
                case '"':
                case '/':
                case '\\':
                case '|':
                case '?':
                case '*':
                    return false;
            }

            if (c == ' ' && i + 1 < name.Length && name[i + 1] == ' ')
            {
                // Reject consecutive spaces — keeps the name "single internal spaces only".
                return false;
            }

            // Allowed: ASCII letter, digit, '-', '_', ' '. Anything else (including most
            // punctuation and non-ASCII) is rejected to keep names file-system safe and
            // identity-friendly.
            var isLetterOrDigit = char.IsLetterOrDigit(c);
            if (!isLetterOrDigit && c != '-' && c != '_' && c != ' ')
            {
                return false;
            }
        }

        if (string.Equals(name, CurrentReservedName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (ReservedWindowsNames.Contains(name))
        {
            return false;
        }

        return true;
    }

    // ─── Private helpers ───────────────────────────────────────────────────────────────

    private void EnsureValidName(string name)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException($"Invalid macro name: '{name}'.", nameof(name));
        }
    }

    private string ResolveScopeFolder(MacroScope scope)
    {
        switch (scope)
        {
            case MacroScope.Global:
                EnsureGlobalEnabled();
                return _globalNamedFolder;
            case MacroScope.Repo:
                if (!TryGetRepoFolder(out var repo))
                {
                    throw new InvalidOperationException("No repo scope available — solution is not loaded.");
                }
                return repo!;
            default:
                throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown macro scope.");
        }
    }

    private void EnsureGlobalEnabled()
    {
        if (!_globalEnabled)
        {
            throw new InvalidOperationException(
                "This FileSystemMacroStore was constructed in repo-only mode; global / current.csx operations are not available.");
        }
    }

    private bool TryGetRepoFolder(out string? folder)
    {
        folder = _repoFolderProvider?.Invoke();
        return !string.IsNullOrEmpty(folder);
    }

    private SemaphoreSlim GetNamedLock(MacroScope scope, string name)
    {
        // Scope-qualify the key so a global "Foo" and a repo "Foo" don't share a lock —
        // they're separate files and writes to one shouldn't stall writes to the other.
        var key = scope.ToString() + "::" + name.ToUpperInvariant();
        return _namedLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
    }

    private void RaiseLibraryChanged(MacroLibraryChangedEventArgs args)
    {
        _libraryChanged?.Invoke(this, args);
    }

    private static void SwapIntoPlace(string tempPath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            File.Replace(tempPath, destinationPath, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tempPath, destinationPath);
        }
    }

    private static void TryDeleteSilently(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup — don't mask the original failure.
        }
    }

    private static bool PathsEqualOnDisk(string a, string b)
    {
        // Windows file systems are case-insensitive in practice; this only matters for
        // a self-rename like "Foo" → "foo" where File.Exists(destination) is true even
        // though it's the same physical file.
        return string.Equals(
            Path.GetFullPath(a),
            Path.GetFullPath(b),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds a <see cref="MacroEntry"/> for an existing file. Returns <see langword="null"/>
    /// when the file disappeared mid-enumeration (a race with an external delete is not a
    /// listing failure). Header parsing failures degrade to defaults rather than dropping
    /// the entry: the user still sees the macro in the tool window, just without metadata.
    /// </summary>
    private static MacroEntry? TryBuildEntry(string path, string name, MacroScope scope)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }
        }
        catch (IOException)
        {
            // Race with an external delete: skip rather than fail the listing.
            return null;
        }

        var (stepCount, triggers) = ParseHeader(path);

        return new MacroEntry(
            Name: name,
            Scope: scope,
            Path: path,
            StepCount: stepCount,
            Modified: new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            SizeBytes: info.Length,
            Triggers: triggers);
    }

    /// <summary>
    /// Reads the leading comment block of a <c>.csx</c> file (capped at
    /// <see cref="HeaderReadBudgetBytes"/> bytes) and extracts the <c>// Steps: N</c>
    /// counter and <c>// @trigger</c> directives. Failures degrade to defaults so a
    /// malformed file never crashes enumeration.
    /// </summary>
    private static (int StepCount, IReadOnlyList<TriggerBinding> Triggers) ParseHeader(string path)
    {
        try
        {
            string headerText = ReadHeaderText(path);
            int stepCount = ParseStepCount(headerText);
            var triggers = TriggerDirectiveParser.Parse(headerText);
            return (stepCount, triggers);
        }
        catch (Exception)
        {
            // Any failure reading or parsing the header (IO, access, decoder errors,
            // exotic file system quirks) degrades to defaults rather than dropping the
            // entry from the listing. The user still sees the macro in the tool window
            // — they just don't get the step count / trigger badges until they fix the
            // file. A throw here would propagate to ListAsync and crash the whole panel.
            return (0, new[] { TriggerBinding.Manual });
        }
    }

    /// <summary>
    /// Reads at most <see cref="HeaderReadBudgetBytes"/> bytes from the head of
    /// <paramref name="path"/>. Returns an empty string when the file is shorter or
    /// unreadable. Exposed at <see langword="internal"/> visibility for unit tests that
    /// pin the parser behaviour without going through full ListAsync enumeration.
    /// </summary>
    internal static string ReadHeaderText(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[HeaderReadBudgetBytes];
        int read = stream.Read(buffer, 0, buffer.Length);
        if (read == 0)
        {
            return string.Empty;
        }

        // Strip a leading UTF-8 BOM so the first comment line parses cleanly.
        int offset = 0;
        if (read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
        {
            offset = 3;
        }

        return Encoding.UTF8.GetString(buffer, offset, read - offset);
    }

    /// <summary>
    /// Extracts the integer N from the first <c>// Steps: N</c> comment line in
    /// <paramref name="headerText"/>. Returns 0 when no such line exists or the integer
    /// could not be parsed. Exposed at <see langword="internal"/> visibility for tests.
    /// </summary>
    internal static int ParseStepCount(string headerText)
    {
        if (string.IsNullOrEmpty(headerText))
        {
            return 0;
        }

        // Walk the file line by line; bail out as soon as we leave the leading comment block
        // since trigger directives and the steps counter only ever live in the header.
        using var reader = new StringReader(headerText);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (!trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                // Hit code — the comment header is over. Step counter must come before code.
                break;
            }

            // Strip the "//" then any whitespace, then look for "Steps:".
            var commentBody = trimmed.Substring(2).TrimStart();
            const string marker = "Steps:";
            if (commentBody.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                var valueText = commentBody.Substring(marker.Length).Trim();
                if (int.TryParse(valueText, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed >= 0)
                {
                    return parsed;
                }
                return 0;
            }
        }

        return 0;
    }

    /// <summary>
    /// Manually raises <see cref="LibraryChanged"/>. Reserved for the file-system watcher
    /// so external edits surface through the same event. Intentionally <c>internal</c>:
    /// external callers go through the Save / Delete / Rename APIs.
    /// </summary>
    internal void RaiseLibraryChangedForWatcher(MacroLibraryChangedEventArgs args)
    {
        if (args is null)
        {
            throw new ArgumentNullException(nameof(args));
        }

        RaiseLibraryChanged(args);
    }

    // Materializing the IEnumerable<KeyValuePair<...>> just for an Equals against the
    // empty descriptor list keeps the tests' "concurrent same-name" assertion readable.
    internal int NamedLockCount => _namedLocks.Count;

    // Test hook: lets tests probe ListAsync without hitting the public scope folder when
    // they want to assert ordering or filtering on a hand-populated directory.
    internal IEnumerable<string> EnumerateRawCsxFiles(MacroScope scope)
    {
        var folder = ResolveScopeFolder(scope);
        if (!Directory.Exists(folder))
        {
            return Enumerable.Empty<string>();
        }
        return Directory.EnumerateFiles(folder, "*" + MacroExtension, SearchOption.TopDirectoryOnly);
    }

    // ─── IDisposable ───────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_watcherSync)
        {
            _globalWatcher?.Dispose();
            _globalWatcher = null;
            _repoWatcher?.Dispose();
            _repoWatcher = null;
        }

        var t = Interlocked.Exchange(ref _debounceTimer, null);
        t?.Dispose();
    }

    // ─── FileSystemWatcher helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Ensures the <see cref="FileSystemWatcher"/> instances are running for every folder
    /// that currently exists on disk. Called lazily on the first
    /// <see cref="LibraryChanged"/> subscription so watchers are only created when
    /// something actually subscribes.
    /// </summary>
    /// <remarks>
    /// NOTE: The repo watcher binds to the path returned by the <em>first</em> invocation
    /// of <c>repoFolderProvider</c> at which the folder exists on disk. If the provider
    /// returns a different path later (e.g., the user switches solutions without closing
    /// the IDE), the watcher does <em>not</em> restart — named-macro operations route to
    /// the new path correctly, but external file changes in the new path will not raise
    /// <see cref="LibraryChanged"/>. Track via <c>m5-watcher-restart-on-solution-change</c>.
    /// </remarks>
    private void EnsureWatchersStarted()
    {
        lock (_watcherSync)
        {
            if (_globalEnabled && _globalWatcher == null && Directory.Exists(_globalNamedFolder))
            {
                _globalWatcher = StartWatcher(_globalNamedFolder, MacroScope.Global);
            }

            var repoFolder = _repoFolderProvider?.Invoke();
            if (_repoWatcher == null && !string.IsNullOrEmpty(repoFolder) && Directory.Exists(repoFolder!))
            {
                _repoWatcher = StartWatcher(repoFolder!, MacroScope.Repo);
            }
        }
    }

    private FileSystemWatcher StartWatcher(string folder, MacroScope scope)
    {
        var w = new FileSystemWatcher(folder, "*.csx")
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        w.Created += (_, e) => OnFsEvent(scope, e.FullPath, MacroLibraryChangeKind.Added);
        w.Deleted += (_, e) => OnFsEvent(scope, e.FullPath, MacroLibraryChangeKind.Removed);
        w.Changed += (_, e) => OnFsEvent(scope, e.FullPath, MacroLibraryChangeKind.Modified);
        w.Renamed += (_, e) =>
        {
            // Old path may be a .tmp file (temp+swap pattern) — only raise Removed for
            // actual .csx files so we don't enqueue events for internal temp names.
            if (e.OldFullPath.EndsWith(MacroExtension, StringComparison.OrdinalIgnoreCase))
            {
                OnFsEvent(scope, e.OldFullPath, MacroLibraryChangeKind.Removed);
            }
            if (e.FullPath.EndsWith(MacroExtension, StringComparison.OrdinalIgnoreCase))
            {
                OnFsEvent(scope, e.FullPath, MacroLibraryChangeKind.Added);
            }
        };
        w.Error += OnWatcherError;
        w.EnableRaisingEvents = true;
        return w;
    }

    private void OnFsEvent(MacroScope scope, string fullPath, MacroLibraryChangeKind kind)
    {
        if (_recentSelfWrites.ContainsKey(fullPath))
        {
            // Storage already raised LibraryChanged synchronously for this write — skip.
            return;
        }

        var name = Path.GetFileNameWithoutExtension(fullPath);
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        // Skip current.csx — it lives outside the named-macro scope folders and is not
        // part of the macro library. Guard here for safety even though the watcher is
        // pointed at the named subfolder.
        if (string.Equals(name, CurrentReservedName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Coalesce events on the same path: Added + Modified → keep Added (a newly created
        // file that received extra write events is still a net-new file from the observer's
        // perspective). Removed always supersedes everything.
        if (kind != MacroLibraryChangeKind.Removed &&
            _pending.TryGetValue(fullPath, out var existing) &&
            existing.Kind == MacroLibraryChangeKind.Added)
        {
            // Keep Added, ignore the subsequent Modified.
            return;
        }

        _pending[fullPath] = new PendingChange(kind, scope, name, null);
        EnsureDebounceTimerRunning();
    }

    private void EnsureDebounceTimerRunning()
    {
        if (_debounceTimer != null)
        {
            return;
        }

        var t = new System.Timers.Timer(200) { AutoReset = false };
        t.Elapsed += (_, _) => FlushPending();

        // CAS: only one thread wins; the loser disposes its extra timer.
        if (Interlocked.CompareExchange(ref _debounceTimer, t, null) != null)
        {
            t.Dispose();
            return;
        }

        t.Start();
    }

    private void FlushPending()
    {
        foreach (var kvp in _pending)
        {
            if (_pending.TryRemove(kvp.Key, out var p))
            {
                try
                {
                    _libraryChanged?.Invoke(this, new MacroLibraryChangedEventArgs(p.Kind, p.Scope, p.Name, p.OldName));
                }
                catch
                {
                    // Don't let a subscriber exception kill the watcher thread.
                }
            }
        }

        var t = Interlocked.Exchange(ref _debounceTimer, null);
        t?.Dispose();

        // Re-arm if more events arrived while we were flushing.
        if (!_pending.IsEmpty)
        {
            EnsureDebounceTimerRunning();
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // Watcher buffer overflow or directory deleted — restart after a short delay.
        _ = Task.Delay(1000).ContinueWith(_ => RestartWatchers(), TaskScheduler.Default);
    }

    private void RestartWatchers()
    {
        lock (_watcherSync)
        {
            var old = _globalWatcher;
            _globalWatcher = null;
            old?.Dispose();

            old = _repoWatcher;
            _repoWatcher = null;
            old?.Dispose();
        }

        EnsureWatchersStarted();
    }

    private void RegisterSelfWrite(string fullPath)
    {
        _recentSelfWrites.TryAdd(fullPath, 0);
        // Use a named parameter to avoid the lambda `_` shadowing the `out _` discard.
        _ = Task.Delay(500).ContinueWith(tsk => _recentSelfWrites.TryRemove(fullPath, out _), TaskScheduler.Default);
    }
}
