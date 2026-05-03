using System;

namespace Macros.Engine.Recording;

/// <summary>
/// Discriminated base of a single captured macro step. Each subtype represents one observable
/// user action that the code generator (M2 codegen todo) will translate into one or more
/// helper calls in the emitted <c>.csx</c>.
/// </summary>
/// <remarks>
/// Modelled as an abstract <c>record class</c> so callers can <c>switch</c> on the concrete
/// subtype with exhaustive pattern matching while still benefiting from value equality in tests.
/// New step kinds (e.g. <c>TextEditStep</c> from <c>m2-text-observer</c>) are added by
/// declaring another nested <c>sealed record</c>.
/// </remarks>
public abstract record class RecordedStep
{
    private protected RecordedStep() { }

    /// <summary>A Visual Studio command observed by <c>CommandObserver</c>.</summary>
    /// <param name="Group">The command set GUID (e.g. <c>VSConstants.GUID_VSStandardCommandSet97</c>).</param>
    /// <param name="Id">The numeric command id within <paramref name="Group"/>.</param>
    /// <param name="Name">
    /// The DTE-resolved friendly name (e.g. <c>Edit.Copy</c>) when available, or
    /// <see langword="null"/> if the command had no DTE registration.
    /// </param>
    public sealed record class CommandStep(Guid Group, uint Id, string? Name) : RecordedStep;

    /// <summary>
    /// A file open captured from the Running Document Table or from a
    /// <c>File.OpenFile</c> command with a non-empty <c>CustomIn</c> argument.
    /// Generated code emits <c>await OpenFileAsync(@"…")</c>.
    /// </summary>
    /// <param name="Path">The full absolute path of the file that was opened.</param>
    public sealed record class FileOpenStep(string Path) : RecordedStep;

    /// <summary>
    /// A file close captured from <c>DocumentEvents.Closed</c>. Generated code emits
    /// <c>await CloseFileAsync(@"…")</c>.
    /// </summary>
    /// <param name="Path">The full absolute path of the file that was closed.</param>
    public sealed record class FileCloseStep(string Path) : RecordedStep;

    // TextEditStep — to be added by m2-text-observer.
}
