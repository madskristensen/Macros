using System;

namespace Macros.Engine.Recording;

/// <summary>
/// Push surface that recording observers (command target, text-buffer listener) call into to
/// hand a raw event to the active <see cref="RecordingSession"/>.
/// </summary>
/// <remarks>
/// <para>
/// The sink is intentionally narrow: observers don't see the session object, they don't see the
/// event list, they only see this interface. That decoupling is what lets unit tests verify
/// capture without spinning up a real <c>MacroService</c>.
/// </para>
/// <para>
/// Implementations MUST be thread-safe — text-buffer change events arrive on the UI thread,
/// command observer callbacks arrive on the UI thread too in practice, but trigger plumbing in
/// later milestones may push events from background threads.
/// </para>
/// </remarks>
public interface IRecordingSink
{
    /// <summary>
    /// Gets a value indicating whether the sink is currently capturing events. Observers
    /// SHOULD short-circuit cheaply when this is <see langword="false"/>; the sink is also
    /// allowed (and expected) to drop events itself if state changed between the observer's
    /// check and the call.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="true"/> iff the owning <see cref="MacroService"/> is in
    /// <see cref="MacroState.Recording"/> AND <see cref="ReplayGuard.IsReplaying"/> is
    /// <see langword="false"/> on the current thread (the latter prevents catastrophic
    /// record-while-replaying loops).
    /// </remarks>
    bool IsCapturing { get; }

    /// <summary>
    /// Records that a Visual Studio command was about to execute (or just executed) on the
    /// shell command chain.
    /// </summary>
    /// <param name="group">The command set GUID (<c>pguidCmdGroup</c> from <c>IOleCommandTarget.Exec</c>).</param>
    /// <param name="id">The numeric command id within <paramref name="group"/>.</param>
    /// <param name="canonicalName">
    /// The friendly DTE command name (e.g. <c>Edit.Copy</c>) when the observer was able to
    /// resolve it, or <see langword="null"/> when no DTE entry exists.
    /// </param>
    /// <remarks>
    /// MUST be a no-op when <see cref="IsCapturing"/> is <see langword="false"/>. Implementations
    /// re-check the capture flag inside the lock to defend against races with state transitions.
    /// </remarks>
    void OnCommand(Guid group, uint id, string? canonicalName);

    /// <summary>
    /// Records that a document was opened during the recording session. This is used to
    /// capture the full file path when a user opens a file via the File &gt; Open dialog (where
    /// the command itself fires with no arguments) or via any other document-opening mechanism.
    /// </summary>
    /// <param name="path">The full absolute path of the document that was opened.</param>
    /// <remarks>
    /// MUST be a no-op when <see cref="IsCapturing"/> is <see langword="false"/>. Callers
    /// SHOULD check <see cref="IsCapturing"/> cheaply before calling; the sink re-checks
    /// inside its lock so a race with a concurrent stop-recording transition is benign.
    /// </remarks>
    void OnFileOpen(string path);

    /// <summary>
    /// Records that a document was closed during the recording session. Captures the full
    /// file path so the generated <c>.csx</c> can replay the close via
    /// <c>CloseFileAsync(@"path")</c>.
    /// </summary>
    /// <param name="path">The full absolute path of the document that was closed.</param>
    /// <remarks>
    /// Same threading and capture-state contract as <see cref="OnFileOpen"/>. Implementations
    /// MUST filter pseudo-document monikers (paths that aren't likely real file-system paths).
    /// </remarks>
    void OnFileClose(string path);

    /// <summary>
    /// Records that a tool window was hidden (closed via the X button or programmatically)
    /// during the recording session. Captures the window caption so the generated <c>.csx</c>
    /// can replay the close via <c>CloseToolWindowAsync("Caption")</c>.
    /// </summary>
    /// <param name="caption">The display caption of the tool window that was closed.</param>
    /// <remarks>
    /// MUST be a no-op when <see cref="IsCapturing"/> is <see langword="false"/>. The caller
    /// is responsible for filtering document frames (we only record tool windows) and for
    /// excluding the Macros tool window itself.
    /// </remarks>
    void OnToolWindowClose(string caption);

    // void OnTextEdit(...) — to be added by m2-text-observer (don't add now, leave for them).
}
