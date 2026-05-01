namespace Macros.Engine.Recording;

/// <summary>
/// Narrow push surface used by the text-edit observer to hand a single
/// <see cref="TextEditStep"/> to the active recording session without taking a hard
/// dependency on the (internal) <c>RecordingSession</c> type.
/// </summary>
/// <remarks>
/// <para>
/// This is a deliberate split from <see cref="IRecordingSink"/>. The two interfaces were
/// authored in parallel by the <c>m2-command-observer</c> and <c>m2-text-observer</c> todos;
/// keeping the text-edit surface separate avoided merge conflicts on
/// <see cref="IRecordingSink"/>. The integration step has <c>RecordingSession</c> implement
/// <em>both</em> interfaces, so the text observer can do
/// <c>service.CurrentSession is ITextEditSink sink</c> and capture without ever naming the
/// concrete session type.
/// </para>
/// <para>
/// Implementations MUST be thread-safe — text-buffer change events arrive on the UI thread
/// in practice but the contract does not promise that.
/// </para>
/// </remarks>
public interface ITextEditSink
{
    /// <summary>
    /// Gets a value indicating whether the sink is currently capturing events. The observer
    /// short-circuits cheaply when this is <see langword="false"/>; the sink is also expected
    /// to drop events itself if state changed between the observer's check and the call.
    /// </summary>
    bool IsCapturing { get; }

    /// <summary>
    /// Records a single <see cref="TextEditStep"/> in the active session.
    /// </summary>
    /// <param name="step">The captured edit. Never <see langword="null"/>.</param>
    /// <remarks>
    /// MUST be a no-op when <see cref="IsCapturing"/> is <see langword="false"/>.
    /// </remarks>
    void OnTextEdit(TextEditStep step);
}
