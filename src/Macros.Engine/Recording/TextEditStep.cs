namespace Macros.Engine.Recording;

/// <summary>
/// A single text-buffer change observed by the text-edit observer. Captures the minimal
/// information the (M2 codegen) <c>StepAggregator</c> needs to fold consecutive single-char
/// inserts into <c>Type("...")</c> calls and emit verbatim splices for everything else.
/// </summary>
/// <param name="OldPosition">
/// The character offset in the buffer (pre-change snapshot) where the change started.
/// </param>
/// <param name="OldLength">
/// The number of characters that were replaced (zero for a pure insertion).
/// </param>
/// <param name="OldText">
/// The text that was removed by the change (empty string for a pure insertion).
/// </param>
/// <param name="NewText">
/// The text that was inserted by the change (empty string for a pure deletion).
/// </param>
/// <remarks>
/// Inherits <see cref="RecordedStep"/> so the (future) <c>StepAggregator</c> /
/// <c>CSharpCodeGenerator</c> can <c>switch</c> exhaustively over every step kind.
/// </remarks>
public sealed record class TextEditStep(int OldPosition, int OldLength, string OldText, string NewText)
    : RecordedStep;
