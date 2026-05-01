using System;
using System.Collections.Generic;

namespace Macros.Engine.Triggers;

/// <summary>
/// <see cref="IMacroTrigger"/> produced when a macro is fired by a VS toolkit event
/// (e.g. <c>Build.SolutionBuildDone</c>).
/// </summary>
internal sealed class VsEventMacroTrigger : IMacroTrigger
{
    /// <inheritdoc />
    public TriggerKind Kind => TriggerKind.VsEvent;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string? CommandName => null;

    /// <inheritdoc />
    public DateTimeOffset FiredAt { get; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> Payload { get; }

    /// <inheritdoc />
    public bool IsManual => false;

    /// <inheritdoc />
    public bool CommandCancelled => false;

    public VsEventMacroTrigger(string canonicalName, DateTimeOffset firedAt, IReadOnlyDictionary<string, object?> payload)
    {
        Name = canonicalName ?? throw new ArgumentNullException(nameof(canonicalName));
        FiredAt = firedAt;
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
    }

    /// <inheritdoc />
    public void CancelCommand() { /* no-op for VS events */ }
}
