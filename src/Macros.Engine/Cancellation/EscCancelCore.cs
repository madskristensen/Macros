namespace Macros.Engine.Cancellation;

/// <summary>
/// Pure stateless logic for Esc-to-cancel: extracted from the WPF <c>KeyProcessor</c> adapter
/// so the decision can be unit-tested without loading VS shell or WPF editor assemblies.
/// </summary>
internal static class EscCancelCore
{
    /// <summary>
    /// If <paramref name="service"/> is currently in <see cref="MacroState.Playing"/>, calls
    /// <see cref="IMacroService.CancelActivePlay"/> and returns <see langword="true"/>.
    /// Returns <see langword="false"/> in any other state or when <paramref name="service"/>
    /// is <see langword="null"/>.
    /// </summary>
    internal static bool TryHandleEscapeKey(IMacroService? service)
    {
        if (service is null || service.State != MacroState.Playing)
            return false;

        service.CancelActivePlay();
        return true;
    }
}
