using System;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;

namespace Macros.Engine.Player;

/// <summary>
/// Single source of truth for the "Macros" Output pane. Caches the first-created
/// <see cref="OutputWindowPane"/> in a process-wide field so every caller (the
/// player, the error renderer, future loggers) writes to the same pane.
/// </summary>
/// <remarks>
/// The toolkit's <c>VS.Windows.CreateOutputWindowPaneAsync(name)</c> assigns a
/// fresh <see cref="Guid"/> on each invocation, so calling it from two different
/// assemblies produced two duplicate "Macros" entries in the Output dropdown.
/// We create the pane once here and hand the same instance back on subsequent
/// calls; <see cref="SemaphoreSlim"/> keeps the first-creation race safe without
/// blocking the UI thread.
/// </remarks>
public static class MacrosOutputPane
{
    /// <summary>Display name shown in the Output window dropdown.</summary>
    public const string Name = "Macros";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static OutputWindowPane? _pane;

    /// <summary>Returns the shared "Macros" Output pane, creating it on first use.</summary>
    public static async Task<OutputWindowPane> GetOrCreateAsync()
    {
        if (_pane is not null)
        {
            return _pane;
        }

        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _pane ??= await VS.Windows.CreateOutputWindowPaneAsync(Name, lazyCreate: false).ConfigureAwait(false);
            return _pane;
        }
        finally
        {
            Gate.Release();
        }
    }
}
