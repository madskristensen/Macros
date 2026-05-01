using System;
using System.ComponentModel.Composition;
using System.Threading;
using Community.VisualStudio.Toolkit;
using Macros.Engine;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;

namespace Macros.Cancellation;

/// <summary>
/// MEF <see cref="IKeyProcessorProvider"/> that attaches an <see cref="EscCancelKeyProcessor"/>
/// to every editable text view. The processor intercepts <c>Esc</c> while a macro is replaying
/// and cancels playback.
/// </summary>
/// <remarks>
/// <para>
/// No wiring in <c>MacrosPackage.InitializeAsync</c> is required — the VSIX manifest already
/// declares the <c>MefComponent</c> asset, so the MEF composition container discovers this
/// export automatically.
/// </para>
/// <para>
/// <see cref="IMacroService"/> is resolved once on first view creation and cached in
/// <see cref="ServiceCache"/>. The resolution blocks briefly on the
/// <see cref="JoinableTaskContext.Factory"/> to stay deadlock-safe; subsequent calls are a
/// single volatile read.
/// </para>
/// </remarks>
[Export(typeof(IKeyProcessorProvider))]
[Name("MacrosEscCancel")]
[ContentType("any")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
[Order(Before = "DefaultKeyProcessor")]
internal sealed class EscCancelKeyProcessorProvider : IKeyProcessorProvider
{
    [Import]
    internal JoinableTaskContext JoinableTaskContext { get; set; } = null!;

    /// <inheritdoc />
    public KeyProcessor GetAssociatedProcessor(IWpfTextView wpfTextView)
    {
        if (ServiceCache.Instance is null)
        {
            ServiceCache.Instance = ResolveService();
        }

        return new EscCancelKeyProcessor(ServiceCache.Instance);
    }

    private IMacroService ResolveService()
    {
        return JoinableTaskContext.Factory.Run(
            () => VS.GetRequiredServiceAsync<IMacroService, IMacroService>());
    }
}

/// <summary>Process-wide cache for the lazily-resolved <see cref="IMacroService"/>.</summary>
internal static class ServiceCache
{
    private static volatile IMacroService? s_instance;

    internal static IMacroService? Instance
    {
        get => s_instance;
        set => s_instance = value;
    }
}
