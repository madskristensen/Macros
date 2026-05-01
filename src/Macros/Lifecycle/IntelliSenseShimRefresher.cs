using System;
using System.Threading;
using Macros.Engine.Scripting;

namespace Macros.Lifecycle;

/// <summary>
/// Owns the lifecycle of the IntelliSense shim file in BOTH the global and
/// per-repo macro stores. Constructed once during package init; subscribes to
/// <see cref="SolutionContextTracker.SolutionChanged"/> to seed / refresh the
/// repo shim whenever a solution opens.
///
/// Thread safety: Write calls run on whichever thread invokes them. The
/// underlying writer is safe (purely path computations + atomic file replace).
/// </summary>
internal sealed class IntelliSenseShimRefresher : IDisposable
{
    private readonly Func<string> _globalFolderProvider;
    private readonly Func<string?> _repoFolderProvider;
    private readonly Action<string, Exception> _onError;
    private SolutionContextTracker? _tracker;
    private bool _disposed;

    public IntelliSenseShimRefresher(
        Func<string> globalFolderProvider,
        Func<string?> repoFolderProvider,
        Action<string, Exception>? onError = null)
    {
        _globalFolderProvider = globalFolderProvider ?? throw new ArgumentNullException(nameof(globalFolderProvider));
        _repoFolderProvider = repoFolderProvider ?? throw new ArgumentNullException(nameof(repoFolderProvider));
        _onError = onError ?? ((_, _) => { });
    }

    /// <summary>Refresh the global shim. Safe to call multiple times — idempotent.</summary>
    public void RefreshGlobal()
    {
        try
        {
            var root = _globalFolderProvider();
            if (string.IsNullOrWhiteSpace(root)) return;
            IntelliSenseShimWriter.Write(root);
        }
        catch (Exception ex) when (!IsCritical(ex))
        {
            _onError("global", ex);
        }
    }

    /// <summary>Refresh the repo shim if a solution is currently open.</summary>
    public void RefreshRepo()
    {
        try
        {
            var root = _repoFolderProvider();
            if (string.IsNullOrWhiteSpace(root)) return;
            IntelliSenseShimWriter.Write(root!);
        }
        catch (Exception ex) when (!IsCritical(ex))
        {
            _onError("repo", ex);
        }
    }

    public void AttachToTracker(SolutionContextTracker tracker)
    {
        if (_tracker is not null) throw new InvalidOperationException("Already attached.");
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _tracker.SolutionChanged += OnSolutionChanged;
    }

    private void OnSolutionChanged(object? sender, EventArgs e) => RefreshRepo();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_tracker is not null) _tracker.SolutionChanged -= OnSolutionChanged;
    }

    private static bool IsCritical(Exception ex) =>
        ex is OutOfMemoryException or StackOverflowException or ThreadAbortException;
}
