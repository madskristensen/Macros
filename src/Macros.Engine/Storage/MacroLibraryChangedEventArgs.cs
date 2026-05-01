using System;

namespace Macros.Engine.Storage;

/// <summary>
/// Categorises the on-disk change that triggered a
/// <see cref="IMacroStore.LibraryChanged"/> event.
/// </summary>
public enum MacroLibraryChangeKind
{
    /// <summary>A new <c>.csx</c> file appeared in a scope folder.</summary>
    Added,

    /// <summary>An existing <c>.csx</c> file was removed.</summary>
    Removed,

    /// <summary>An existing <c>.csx</c> file's contents changed in place.</summary>
    Modified,

    /// <summary>An existing <c>.csx</c> file was renamed; <see cref="MacroLibraryChangedEventArgs.OldName"/> is set.</summary>
    Renamed,
}

/// <summary>
/// Payload for <see cref="IMacroStore.LibraryChanged"/>. Raised both by the storage
/// implementation itself (after a successful Save / Delete / Rename) and by the
/// out-of-band file-system watcher (next wave) when an external editor mutates a file.
/// </summary>
/// <remarks>
/// The event is raised <strong>synchronously on whichever thread performed the change</strong>.
/// Subscribers (the storage watcher, the tool window) are responsible for marshalling to the
/// UI thread before touching WPF state.
/// </remarks>
public sealed class MacroLibraryChangedEventArgs : EventArgs
{
    /// <summary>Initializes a new instance of the <see cref="MacroLibraryChangedEventArgs"/> class.</summary>
    /// <param name="kind">What kind of change occurred.</param>
    /// <param name="scope">Which scope the affected macro lives in.</param>
    /// <param name="name">Current name of the affected macro (for <see cref="MacroLibraryChangeKind.Renamed"/>, the new name).</param>
    /// <param name="oldName">Previous name when <paramref name="kind"/> is <see cref="MacroLibraryChangeKind.Renamed"/>; otherwise <see langword="null"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or whitespace.</exception>
    public MacroLibraryChangedEventArgs(
        MacroLibraryChangeKind kind,
        MacroScope scope,
        string name,
        string? oldName = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name must be non-empty.", nameof(name));
        }

        Kind = kind;
        Scope = scope;
        Name = name;
        OldName = oldName;
    }

    /// <summary>Gets the kind of change that occurred.</summary>
    public MacroLibraryChangeKind Kind { get; }

    /// <summary>Gets the scope the affected macro lives in.</summary>
    public MacroScope Scope { get; }

    /// <summary>Gets the current name of the affected macro.</summary>
    public string Name { get; }

    /// <summary>Gets the previous name when <see cref="Kind"/> is <see cref="MacroLibraryChangeKind.Renamed"/>.</summary>
    public string? OldName { get; }
}
