using System;

namespace Macros.Engine;

/// <summary>
/// Event payload raised by <see cref="IMacroService.RecordingSaved"/> after the engine's
/// background save lands the freshly-recorded macro into the named library.
/// </summary>
public sealed class RecordingSavedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingSavedEventArgs"/> class.
    /// </summary>
    /// <param name="path">The absolute on-disk path of the saved <c>.csx</c> file.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    public RecordingSavedEventArgs(string path)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
    }

    /// <summary>
    /// Gets the absolute on-disk path of the just-saved <c>.csx</c> file. UI consumers
    /// (e.g. the <c>Stop</c> command) open this path in the VS editor so the user can
    /// review or edit the recorded macro immediately after stopping.
    /// </summary>
    public string Path { get; }
}
