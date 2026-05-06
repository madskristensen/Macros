using System.ComponentModel.Composition;
using Macros;
using Macros.Engine;
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
/// MEF can instantiate this provider — and create the per-view processor — before
/// <c>MacrosPackage.InitializeAsync</c> has run (e.g. when another package restores documents
/// during shell startup). To stay robust, <see cref="IMacroService"/> is resolved lazily on
/// each <c>Esc</c> keystroke and cached once successfully obtained. If the package isn't
/// loaded yet, the processor is a harmless no-op until it is.
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
        return new EscCancelKeyProcessor(ResolveServiceOrNull);
    }

    private static IMacroService? ResolveServiceOrNull()
    {
        if (ServiceCache.Instance is { } cached)
        {
            return cached;
        }

        var package = MacrosPackage.Instance;
        if (package is null)
        {
            return null;
        }

        var service = package.JoinableTaskFactory.Run(async () =>
            await package.GetServiceAsync(typeof(IMacroService)) as IMacroService);

        if (service is not null)
        {
            ServiceCache.Instance = service;
        }

        return service;
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
