using System;
using System.ComponentModel.Composition;
using Macros;
using Macros.Engine;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
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
    /// <inheritdoc />
    public KeyProcessor GetAssociatedProcessor(IWpfTextView wpfTextView)
    {
        if (ServiceCache.Instance is null)
        {
            ServiceCache.Instance = ResolveService();
        }

        return new EscCancelKeyProcessor(ServiceCache.Instance);
    }

    private static IMacroService ResolveService()
    {
        var package = MacrosPackage.Instance ?? throw new InvalidOperationException(
            "MacrosPackage is not initialized — cannot resolve IMacroService.");
        return package.JoinableTaskFactory.Run(async () =>
            await package.GetServiceAsync(typeof(IMacroService)) as IMacroService
                ?? throw new InvalidOperationException(
                    "IMacroService is not registered in the package container."));
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
