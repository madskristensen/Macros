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
internal sealed class EscCancelKeyProcessor : KeyProcessor
{
    private readonly IMacroService _service;

    internal EscCancelKeyProcessor(IMacroService service)
    {
        _service = service;
    }

    /// <inheritdoc />
    public override void KeyDown(KeyEventArgs args)
    {
        // Delegate to the engine-layer helper so the identical logic can be tested without
        // loading VS shell or WPF editor assemblies.
        if (args.Key == Key.Escape && EscCancelCore.TryHandleEscapeKey(_service))
        {
            args.Handled = true;
        }
    }
}
