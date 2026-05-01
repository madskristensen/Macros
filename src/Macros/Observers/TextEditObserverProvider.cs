using System;
using System.ComponentModel.Composition;
using System.Threading;
using Community.VisualStudio.Toolkit;
using Macros;
using Macros.Engine;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;

namespace Macros.Observers;

/// <summary>
/// MEF entry point for the text-edit recorder. Visual Studio calls
/// <see cref="TextViewCreated(IWpfTextView)"/> once for every editable text view that
/// opens — code, markup, settings files, anything that derives from the universal
/// <c>"any"</c> content type. We attach a lightweight per-view <see cref="TextEditObserver"/>
/// that pushes <see cref="Macros.Engine.Recording.TextEditStep"/>s into the active
/// recording session.
/// </summary>
/// <remarks>
/// <para>
/// We pin to <see cref="PredefinedTextViewRoles.Editable"/> so we don't waste a subscription
/// on read-only diff / preview / signature-help views — those views never produce edits the
/// user would want to record.
/// </para>
/// <para>
/// The <see cref="IMacroService"/> resolution is fire-and-forget on first view creation:
/// resolving a proffered service via <see cref="VS"/> is async, but the
/// <see cref="ITextBuffer.Changed"/> handler must run synchronously on the UI thread. We
/// therefore cache the service in a static field and let the very first few text-buffer
/// changes after package load fall through as no-ops while the cache warms. Acceptable —
/// the package auto-loads on shell startup so the window is microseconds.
/// </para>
/// </remarks>
[Export(typeof(IWpfTextViewCreationListener))]
[ContentType("any")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
internal sealed class TextEditObserverProvider : IWpfTextViewCreationListener
{
    private static IMacroService? s_service;
    private static int s_resolveStarted;

    /// <summary>
    /// MEF-imported <see cref="JoinableTaskContext"/>. The editor host always exports this
    /// in a hosted Visual Studio process, so the import is satisfied at composition time.
    /// We use it to schedule the one-shot async <see cref="IMacroService"/> resolve from a
    /// MEF callback (which is not a package context, so we can't use the package's JTF).
    /// </summary>
    [Import]
    internal JoinableTaskContext JoinableTaskContext { get; set; } = null!;

    /// <inheritdoc />
    public void TextViewCreated(IWpfTextView textView)
    {
        if (textView is null)
        {
            return;
        }

        EnsureServiceResolutionStarted(JoinableTaskContext);

        // The observer wires itself up; we don't keep a reference. Lifetime is bounded by
        // the view: textView.Closed unhooks the buffer subscription and lets the observer
        // be GC'd along with the view.
        _ = new TextEditObserver(textView, GetService);
    }

    /// <summary>Hot-path accessor — a single volatile read.</summary>
    internal static IMacroService? GetService() => Volatile.Read(ref s_service);

    private static void EnsureServiceResolutionStarted(JoinableTaskContext jtc)
    {
        // Single-shot kickoff. Interlocked.Exchange returns the previous value; if it was
        // already 1 someone else started the resolve.
        if (Interlocked.Exchange(ref s_resolveStarted, 1) != 0)
        {
            return;
        }

        // We deliberately use the MEF-imported JoinableTaskContext (NOT
        // ThreadHelper.JoinableTaskFactory) to satisfy VSSDK007 — VSSDK007 only allows
        // fire-and-forget on a context owned by the caller, not the global one. The
        // continuation only writes a static field and exits; it never re-enters the UI
        // thread.
        _ = jtc.Factory.RunAsync(async () =>
        {
            try
            {
                var package = MacrosPackage.Instance
                    ?? throw new InvalidOperationException(
                        "MacrosPackage is not initialized — cannot resolve IMacroService.");
                var resolved = await package.GetServiceAsync(typeof(IMacroService)) as IMacroService
                    ?? throw new InvalidOperationException(
                        "IMacroService is not registered in the package container.");
                Volatile.Write(ref s_service, resolved);
            }
            catch (Exception ex)
            {
                await ex.LogAsync();

                // Allow a future view-create to retry; a transient package-load hiccup must
                // not permanently disable text-edit recording for the entire VS session.
                Interlocked.Exchange(ref s_resolveStarted, 0);
            }
        });
    }
}

// PACKAGE-WIRING-REQUIRED: none.
//
// This part is discovered automatically by the editor's MEF composition container — the
// VSIX manifest already declares the package's MefComponent asset (see
// source.extension.vsixmanifest), so simply having the [Export] attribute on this class is
// enough. No MacrosPackage.InitializeAsync change is required for this todo.
//
// Integration follow-up (separate todo, not this one):
//   * Macros.Engine.Recording.RecordingSession must additionally implement
//     Macros.Engine.Recording.ITextEditSink — currently it only implements IRecordingSink,
//     so the `service.CurrentSession is ITextEditSink sink` pattern matches at runtime
//     only after that interface is added. Until then, this observer is a runtime no-op
//     for text edits. Compilation is unaffected.
