using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine.Storage;
using Macros.Lifecycle;
using Macros.Options;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

namespace Macros.Trust;

internal sealed class TrustPromptService
{
    internal const string PromptTitle = "Repo Macros";
    internal const string PromptMessage = "This solution contains macros that want to run automatically. Allow macros from this solution to run?";

    private readonly JoinableTaskFactory _jtf;
    private readonly AsyncPackage? _package;
    private readonly Func<string?> _solutionPathProvider;
    private readonly Func<Task<MacrosOptions>> _optionsProvider;
    private readonly Func<string, CancellationToken, Task<bool>> _showPromptAsync;

    private readonly object _sync = new();
    private readonly Dictionary<string, Task<bool>> _pendingPrompts = new(StringComparer.OrdinalIgnoreCase);

    public TrustPromptService(
        JoinableTaskFactory jtf,
        AsyncPackage? package = null,
        Func<string?>? solutionPathProvider = null,
        Func<Task<MacrosOptions>>? optionsProvider = null,
        Func<string, CancellationToken, Task<bool>>? showPromptAsync = null)
    {
        _jtf = jtf ?? throw new ArgumentNullException(nameof(jtf));
        _package = package;
        _solutionPathProvider = solutionPathProvider ?? (() => SolutionContextTracker.Current?.GetCurrentSolutionPath());
        _optionsProvider = optionsProvider ?? MacrosOptions.GetLiveInstanceAsync;
        _showPromptAsync = showPromptAsync ?? ShowPromptAsync;
    }

    public async Task<bool> IsAllowedAsync(MacroEntry entry, CancellationToken cancellationToken = default)
    {
        if (entry is null)
        {
            return false;
        }

        var solutionPath = _solutionPathProvider();
        var options = await _optionsProvider().ConfigureAwait(true);
        if (TrustGate.IsAllowed(entry, options, solutionPath))
        {
            return true;
        }

        if (entry.Scope != MacroScope.Repo || string.IsNullOrEmpty(solutionPath))
        {
            return false;
        }

        if (options.IsSolutionBlocked(solutionPath))
        {
            return false;
        }

        return await GetOrCreatePromptAsync(Canonicalize(solutionPath!), cancellationToken).ConfigureAwait(true);
    }

    private Task<bool> GetOrCreatePromptAsync(string solutionPath, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_pendingPrompts.TryGetValue(solutionPath, out var pending))
            {
                return pending;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingPrompts[solutionPath] = tcs.Task;
            _ = RunPromptAsync(solutionPath, tcs, cancellationToken);
            return tcs.Task;
        }
    }

    private async Task RunPromptAsync(string solutionPath, TaskCompletionSource<bool> tcs, CancellationToken cancellationToken)
    {
        try
        {
            var result = await PromptAndPersistAsync(solutionPath, cancellationToken).ConfigureAwait(true);
            tcs.TrySetResult(result);
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
        finally
        {
            lock (_sync)
            {
                if (_pendingPrompts.TryGetValue(solutionPath, out var current) && ReferenceEquals(current, tcs.Task))
                {
                    _pendingPrompts.Remove(solutionPath);
                }
            }
        }
    }

    private async Task<bool> PromptAndPersistAsync(string solutionPath, CancellationToken cancellationToken)
    {
        var options = await _optionsProvider().ConfigureAwait(true);
        if (options.IsSolutionTrusted(solutionPath))
        {
            return true;
        }

        if (options.IsSolutionBlocked(solutionPath))
        {
            return false;
        }

        var allow = await _showPromptAsync(solutionPath, cancellationToken).ConfigureAwait(true);
        if (allow)
        {
            options.TrustSolution(solutionPath);
        }
        else
        {
            options.BlockSolution(solutionPath);
        }

        try
        {
            await options.SaveAsync().ConfigureAwait(true);
        }
        catch
        {
            // The in-memory trust decision is already updated, so keep the user's answer
            // for the rest of the session even if the settings store write fails.
        }

        return allow;
    }

    private async Task<bool> ShowPromptAsync(string solutionPath, CancellationToken cancellationToken)
    {
        if (_package is null)
        {
            throw new InvalidOperationException("A package is required when no custom prompt callback is supplied.");
        }

        await _jtf.SwitchToMainThreadAsync(cancellationToken);

        var result = VsShellUtilities.ShowMessageBox(
            _package,
            PromptMessage,
            PromptTitle,
            OLEMSGICON.OLEMSGICON_WARNING,
            OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND);

        return result == (int)VSConstants.MessageBoxResult.IDYES;
    }

    private static string Canonicalize(string solutionPath)
    {
        return Path.GetFullPath(solutionPath).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
    }
}
