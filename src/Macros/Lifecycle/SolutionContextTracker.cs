using System;
using System.IO;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Macros.Engine.Storage;
using Macros.Options;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace Macros.Lifecycle;

/// <summary>
/// Tracks the active solution directory by subscribing to VS's solution open / close
/// events, and exposes it as a thread-safe accessor that the repo-folder provider passed
/// to <see cref="FileSystemMacroStore"/> / <see cref="RepoMacroStore"/> can call on every
/// named-macro operation.
/// </summary>
/// <remarks>
/// <para>
/// Closes the M3 deferred TODO in <c>MacrosPackage</c> that read
/// <c>// TODO(m3-storage-watcher): subscribe to VS.Events.SolutionEvents.OnAfterOpenSolution
/// / OnAfterCloseSolution to update _solutionDirectory.</c>
/// </para>
/// <para>
/// Lifecycle: constructed once during package init via <see cref="InitializeAsync"/>;
/// kept alive on a package field so the toolkit's event subscriptions stay rooted; disposed
/// when the package shuts down. The instance also primes itself on startup so a package
/// that auto-loads after a solution is already open sees the correct directory immediately.
/// </para>
/// <para>
/// Thread-safety: <see cref="GetCurrentSolutionDirectory"/> and the corresponding
/// <see cref="GetCurrentRepoMacrosFolder"/> may be called from any thread (the
/// repo-folder provider is invoked by background-thread storage operations); the
/// directory field is guarded by a <see langword="lock"/> so reads see a consistent value
/// even while an open / close handler is mid-update.
/// </para>
/// </remarks>
internal sealed class SolutionContextTracker : IDisposable
{
    private readonly object _sync = new();
    private readonly JoinableTaskFactory? _jtf;
    private string? _solutionDirectory;
    private SolutionEvents? _events;

    private SolutionContextTracker(JoinableTaskFactory jtf)
    {
        _jtf = jtf;
    }

    // Test-only ctor — no JTF available because tests don't need to dispatch async work
    // back to the UI thread. Suppress the analyzer here rather than threading a JTF
    // through every test setup; OnSolutionOpened guards against a null _jtf.
#pragma warning disable VSTHRD012
    private SolutionContextTracker()
    {
        _jtf = null;
    }
#pragma warning restore VSTHRD012

    /// <summary>
    /// The most recently constructed and fully initialised tracker, set by
    /// <see cref="InitializeAsync"/>. Context-menu commands read this to check
    /// <see cref="HasSolution"/> synchronously from <c>BeforeQueryStatus</c> without
    /// blocking the UI thread. <see langword="null"/> until package init has run.
    /// </summary>
    public static SolutionContextTracker? Current { get; internal set; }

    /// <summary>
    /// Returns <see langword="true"/> when a solution is currently open.
    /// Safe to call from any thread; uses the same lock as
    /// <see cref="GetCurrentSolutionDirectory"/>.
    /// </summary>
    public bool HasSolution => GetCurrentSolutionDirectory() != null;

    /// <summary>
    /// Raised whenever the active solution directory changes — open, close, or switch.
    /// Subscribers run synchronously on whichever thread the underlying VS event fires
    /// on; if you need to touch UI, hop to the main thread inside the handler.
    /// </summary>
    public event EventHandler? SolutionChanged;

    /// <summary>
    /// Returns the directory of the currently open solution, or <see langword="null"/>
    /// when no solution is open. Safe to call from any thread.
    /// </summary>
    public string? GetCurrentSolutionDirectory()
    {
        lock (_sync)
        {
            return _solutionDirectory;
        }
    }

    /// <summary>
    /// Returns the absolute path of the per-solution repo macros folder
    /// (<c>&lt;solution&gt;\.vs\&lt;RepoMacrosFolderName&gt;</c>), or <see langword="null"/>
    /// when no solution is open. Resolves the configured folder name from
    /// <see cref="MacrosOptions"/> on every call so an options change takes effect
    /// immediately without needing to re-open the solution.
    /// </summary>
    public string? GetCurrentRepoMacrosFolder()
    {
        var dir = GetCurrentSolutionDirectory();
        var folderName = SafeReadFolderName();
        return MacrosPaths.ResolveRepoFolder(dir, folderName);
    }

    /// <summary>
    /// Constructs a tracker, subscribes to solution open / close events on the toolkit's
    /// <c>VS.Events.SolutionEvents</c> surface, and primes the initial directory from the
    /// currently-loaded solution (if any). Must be awaited on the UI thread or via a
    /// <see cref="JoinableTaskFactory"/>; switches to the main thread internally.
    /// </summary>
    public static async Task<SolutionContextTracker> InitializeAsync(AsyncPackage package)
    {
        _ = package ?? throw new ArgumentNullException(nameof(package));

        await package.JoinableTaskFactory.SwitchToMainThreadAsync();

        var tracker = new SolutionContextTracker(package.JoinableTaskFactory);
        var solnEvents = VS.Events.SolutionEvents;
        solnEvents.OnAfterOpenSolution += tracker.OnSolutionOpened;
        solnEvents.OnAfterCloseSolution += tracker.OnSolutionClosed;
        tracker._events = solnEvents;

        await tracker.RefreshAsync(package.JoinableTaskFactory);
        return tracker;
    }

    /// <summary>
    /// Test-only constructor: lets unit tests drive <see cref="ApplySolutionPath"/>
    /// directly without standing up the VS shell. Subscribers can attach to
    /// <see cref="SolutionChanged"/> to verify event semantics.
    /// </summary>
#pragma warning disable VSTHRD012
    internal static SolutionContextTracker CreateForTests() => new();
#pragma warning restore VSTHRD012

    /// <summary>
    /// Test hook: sets the tracked solution path as if VS had raised the corresponding
    /// open / close event. Pass <see langword="null"/> to simulate a close.
    /// </summary>
    internal void ApplySolutionPath(string? solutionDirectory)
    {
        SetDirectoryAndRaise(NormalizeNullOrEmpty(solutionDirectory));
    }

    private async Task RefreshAsync(JoinableTaskFactory jtf)
    {
        await jtf.SwitchToMainThreadAsync();

        var soln = await VS.Solutions.GetCurrentSolutionAsync();
        var dir = TryGetDirectory(soln?.FullPath);
        SetDirectoryAndRaise(dir);
    }

    private void OnSolutionOpened(Community.VisualStudio.Toolkit.Solution? solution)
    {
        // The toolkit sometimes raises this with a partially-populated Solution before
        // FullPath is set; fall back to a fresh GetCurrentSolutionAsync probe rather
        // than caching a null path. Errors are swallowed because a tracker that fails
        // to refresh would silently break repo-scope storage; FileAndForget surfaces
        // the failure via VS telemetry without taking the package down.
        var jtf = _jtf;
        if (jtf is null)
        {
            // Test-only path (no JTF wired); just snapshot whatever the toolkit gave us.
            SetDirectoryAndRaise(TryGetDirectory(solution?.FullPath));
            return;
        }

        jtf.RunAsync(async () =>
        {
            await jtf.SwitchToMainThreadAsync();
            var path = solution?.FullPath;
            if (string.IsNullOrEmpty(path))
            {
                var fresh = await VS.Solutions.GetCurrentSolutionAsync();
                path = fresh?.FullPath;
            }

            SetDirectoryAndRaise(TryGetDirectory(path));
        }).FileAndForget("Macros/SolutionContextTracker/Opened");
    }

    private void OnSolutionClosed()
    {
        SetDirectoryAndRaise(null);
    }

    private void SetDirectoryAndRaise(string? dir)
    {
        bool changed;
        lock (_sync)
        {
            changed = !string.Equals(_solutionDirectory, dir, StringComparison.OrdinalIgnoreCase);
            _solutionDirectory = dir;
        }

        if (changed)
        {
            SolutionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string? TryGetDirectory(string? fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
        {
            return null;
        }

        try
        {
            return Path.GetDirectoryName(fullPath);
        }
        catch (ArgumentException)
        {
            // Malformed path — treat as no solution rather than crashing the open handler.
            return null;
        }
    }

    private static string? NormalizeNullOrEmpty(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;

    // Reading MacrosOptions.Instance is normally safe, but a unit-test path that
    // exercises the tracker without a populated options provider would crash the
    // accessor. Defaulting to "Macros" keeps the helper total in those scenarios.
    private static string SafeReadFolderName()
    {
        try
        {
            return MacrosOptions.Instance.RepoMacrosFolderName ?? "Macros";
        }
        catch
        {
            return "Macros";
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_events is not null)
        {
            try
            {
                _events.OnAfterOpenSolution -= OnSolutionOpened;
                _events.OnAfterCloseSolution -= OnSolutionClosed;
            }
            catch
            {
                // Shutdown path: the toolkit may already have torn down its event source.
            }

            _events = null;
        }
    }
}
