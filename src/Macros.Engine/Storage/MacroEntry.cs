using System;
using System.Collections.Generic;
using Macros.Engine.Triggers;

namespace Macros.Engine.Storage;

/// <summary>
/// Lightweight metadata snapshot describing a named macro file on disk. Returned by the
/// <see cref="IMacroStore"/> enumeration APIs so the tool window can populate its list view
/// without reading every macro's source up-front.
/// </summary>
/// <remarks>
/// <para>
/// In M3 this record was named <c>MacroDescriptor</c> and exposed only file-system shape
/// (Name, Scope, Path, Modified, Size). M4 extends it with <see cref="StepCount"/> and
/// <see cref="Triggers"/>, both parsed cheaply from the leading comment block of the
/// <c>.csx</c> file at enumeration time. Callers that don't care about either field can
/// construct it with <c>StepCount: 0</c> and <c>Triggers: Array.Empty&lt;TriggerBinding&gt;()</c>.
/// </para>
/// <para>
/// The record is value-equal across every field. Tests rely on this so that comparing
/// two enumerations with the same on-disk content yields equal lists.
/// </para>
/// </remarks>
/// <param name="Name">
/// File stem, without the <c>.csx</c> extension (e.g. <c>"Format-On-Save"</c>). Matches the
/// argument required by every other named-macro API on <see cref="IMacroStore"/>.
/// </param>
/// <param name="Scope">Whether the file lives in the global or repo library.</param>
/// <param name="Path">
/// Absolute path to the underlying <c>.csx</c> file. Stable for the lifetime of the macro
/// (renames mutate it). Useful for opening the file in the VS editor.
/// </param>
/// <param name="StepCount">
/// Number of recorded steps reported by the file's <c>// Steps: N</c> header line.
/// Zero when the file has no header or the header could not be parsed.
/// </param>
/// <param name="Modified">
/// Snapshot of <see cref="System.IO.File.GetLastWriteTimeUtc(string)"/> at enumeration time,
/// promoted to <see cref="DateTimeOffset"/> so callers (UI, telemetry) don't have to
/// re-attach a kind.
/// </param>
/// <param name="SizeBytes">Snapshot of the file size in bytes at enumeration time.</param>
/// <param name="Triggers">
/// Trigger bindings parsed from the file's leading comment block. A macro with no
/// <c>// @trigger</c> directives surfaces a single <see cref="TriggerBinding.Manual"/>
/// entry so the tool window always has at least one badge to render.
/// </param>
public sealed record MacroEntry(
    string Name,
    MacroScope Scope,
    string Path,
    int StepCount,
    DateTimeOffset Modified,
    long SizeBytes,
    IReadOnlyList<TriggerBinding> Triggers);
