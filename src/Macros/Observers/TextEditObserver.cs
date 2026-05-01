using System;
using System.Collections.Generic;
using Macros.Engine;
using Macros.Engine.Recording;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;

namespace Macros.Observers;

/// <summary>
/// Per-view bridge between an editor's <see cref="ITextBuffer.Changed"/> stream and the
/// active <see cref="ITextEditSink"/>. One instance is created by
/// <see cref="TextEditObserverProvider"/> for every editable text view; it self-unhooks
/// when the view closes.
/// </summary>
/// <remarks>
/// <para>
/// The hot path is <see cref="OnTextBufferChanged"/>. Editor buffer changes fire on every
/// keystroke, every paste, every IntelliSense commit — so the early-out chain is ordered
/// by cost ascending:
/// </para>
/// <list type="number">
///   <item><description><see cref="ReplayGuard.IsReplaying"/> — a single thread-static read.
///   This is the most important guard: when our own playback edits the buffer we MUST NOT
///   re-record them or a single replay would feed itself an infinitely-growing macro.</description></item>
///   <item><description>Service-cache read — one volatile field load.</description></item>
///   <item><description><c>service.CurrentSession is ITextEditSink</c> — a single type
///   test; null-fast when not recording.</description></item>
///   <item><description><see cref="ITextEditSink.IsCapturing"/> — re-checked because the
///   sink itself owns the authoritative flag.</description></item>
/// </list>
/// <para>
/// Everything except the final foreach happens before we touch <see cref="ITextChange"/>
/// data — so the cost of a keystroke during normal (non-recording) editing is roughly four
/// field reads.
/// </para>
/// </remarks>
internal sealed class TextEditObserver
{
    private readonly IWpfTextView _textView;
    private readonly Func<IMacroService?> _serviceAccessor;

    /// <summary>
    /// Initializes a new <see cref="TextEditObserver"/> bound to <paramref name="textView"/>
    /// and starts listening for buffer changes.
    /// </summary>
    /// <param name="textView">The view whose buffer this observer watches.</param>
    /// <param name="serviceAccessor">
    /// Returns the cached <see cref="IMacroService"/> instance, or <see langword="null"/> if
    /// the service hasn't been resolved yet. Injected so the provider can share a single
    /// service-cache across all views without forcing this class to know how it was
    /// resolved.
    /// </param>
    public TextEditObserver(IWpfTextView textView, Func<IMacroService?> serviceAccessor)
    {
        _textView = textView ?? throw new ArgumentNullException(nameof(textView));
        _serviceAccessor = serviceAccessor ?? throw new ArgumentNullException(nameof(serviceAccessor));

        _textView.TextBuffer.Changed += OnTextBufferChanged;
        _textView.Closed += OnViewClosed;
    }

    private void OnTextBufferChanged(object sender, TextContentChangedEventArgs e)
    {
        // Guard 1: never record our own playback. ReplayGuard is thread-static; the buffer
        // change fires synchronously on the thread that mutated the text (UI thread for
        // normal user input AND for our scripted playback), so the guard is reliable here.
        if (ReplayGuard.IsReplaying)
        {
            return;
        }

        // Guard 2: service may not have been resolved yet (very early after package load),
        // or may have failed to resolve. Either way, no sink => nothing to record.
        var service = _serviceAccessor();
        if (service is null)
        {
            return;
        }

        // Guard 3: only record while there is an active session AND that session has opted
        // into text-edit recording (m2-text-observer + integration handshake). Until
        // RecordingSession implements ITextEditSink the pattern match returns false and we
        // are a runtime no-op — compilation succeeds either way.
        if (service.CurrentSession is not ITextEditSink sink || !sink.IsCapturing)
        {
            return;
        }

        // Iterate over the change list explicitly so we don't pay LINQ allocations on the
        // hot path. INormalizedTextChangeCollection is itself an IList<ITextChange>.
        IList<ITextChange> changes = e.Changes;
        for (int i = 0; i < changes.Count; i++)
        {
            ITextChange change = changes[i];
            sink.OnTextEdit(new TextEditStep(
                OldPosition: change.OldPosition,
                OldLength: change.OldLength,
                OldText: change.OldText,
                NewText: change.NewText));
        }
    }

    private void OnViewClosed(object sender, EventArgs e)
    {
        _textView.TextBuffer.Changed -= OnTextBufferChanged;
        _textView.Closed -= OnViewClosed;
    }
}
