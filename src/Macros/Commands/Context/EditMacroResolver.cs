using System;
using Macros.Engine.Storage;

namespace Macros.Commands.Context;

/// <summary>
/// Pure, testable helper that resolves whether a macro file can be opened for editing.
/// Separated from <see cref="EditContextCommand"/> to allow unit testing without the VS host.
/// </summary>
internal static class EditMacroResolver
{
    /// <summary>
    /// Validates that <paramref name="desc"/> is non-null and that its file exists on disk.
    /// </summary>
    /// <param name="desc">The selected macro descriptor, or <see langword="null"/>.</param>
    /// <param name="fileExists">
    /// File-existence predicate (typically <see cref="System.IO.File.Exists"/>); injected
    /// for testability.
    /// </param>
    /// <returns>
    /// <c>(true, path, "")</c> when the file is ready to open; <c>(false, path, message)</c>
    /// with a user-facing error message otherwise.
    /// </returns>
    public static (bool ok, string path, string errorMessage) Resolve(
        MacroEntry? desc,
        Func<string, bool> fileExists)
    {
        if (desc is null)
        {
            return (false, "", "No macro selected.");
        }

        if (!fileExists(desc.Path))
        {
            return (false, desc.Path,
                $"Macro file no longer exists: \"{desc.Path}\"\n\n" +
                "The file may have been deleted externally. The macro list will refresh automatically.");
        }

        return (true, desc.Path, "");
    }
}
