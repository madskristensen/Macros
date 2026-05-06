using System;
using System.Windows.Input;
using Macros.Engine;
using Macros.Engine.Cancellation;
using Microsoft.VisualStudio.Text.Editor;

namespace Macros.Cancellation;

/// <summary>
/// Per-view <see cref="KeyProcessor"/> that intercepts <see cref="Key.Escape"/> while a macro
/// is replaying and calls <see cref="IMacroService.CancelActivePlay"/> to abort playback.
/// Created by <see cref="EscCancelKeyProcessorProvider"/> for every editable text view.
/// </summary>
/// <remarks>
/// The service is resolved lazily via <paramref name="serviceResolver"/> on each keystroke so
/// processors created before <c>MacrosPackage</c> has finished initializing become functional
/// once the package is loaded, instead of being permanently inert.
/// </remarks>
internal sealed class EscCancelKeyProcessor : KeyProcessor
{
    private readonly Func<IMacroService?> _serviceResolver;

    internal EscCancelKeyProcessor(Func<IMacroService?> serviceResolver)
    {
        _serviceResolver = serviceResolver;
    }

    /// <inheritdoc />
    public override void KeyDown(KeyEventArgs args)
    {
        if (args.Key != Key.Escape)
        {
            return;
        }

        // Delegate to the engine-layer helper so the identical logic can be tested without
        // loading VS shell or WPF editor assemblies. EscCancelCore tolerates a null service.
        if (EscCancelCore.TryHandleEscapeKey(_serviceResolver()))
        {
            args.Handled = true;
        }
    }
}
