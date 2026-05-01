using System;

namespace Macros.Engine.Storage;

/// <summary>
/// Lightweight metadata snapshot describing a named macro file on disk. Returned by the
/// <see cref="IMacroStorage"/> enumeration APIs so the tool window can populate its list
/// view without reading every macro's source up-front.
/// </summary>
/// <param name="Name">
/// File stem, without the <c>.csx</c> extension (e.g. <c>"Format-On-Save"</c>). Matches the
/// argument required by every other named-macro API on <see cref="IMacroStorage"/>.
/// </param>
/// <param name="Scope">Whether the file lives in the global or repo library.</param>
/// <param name="FilePath">
/// Absolute path to the underlying <c>.csx</c> file. Stable for the lifetime of the macro
/// (renames mutate it). Useful for opening the file in the VS editor.
/// </param>
/// <param name="LastModifiedUtc">
/// Snapshot of <see cref="System.IO.File.GetLastWriteTimeUtc(string)"/> at enumeration time.
/// </param>
/// <param name="SizeBytes">Snapshot of the file size in bytes at enumeration time.</param>
public sealed record MacroDescriptor(
    string Name,
    MacroScope Scope,
    string FilePath,
    DateTime LastModifiedUtc,
    long SizeBytes);
