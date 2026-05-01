using System;
using System.Collections.Generic;

namespace Macros.Engine.Triggers;

/// <summary>
/// Singleton <see cref="IMacroTrigger"/> used when a macro is started by direct user action
/// (toolbar, hotkey, palette, tool window) rather than by an automated trigger.
/// </summary>
internal sealed class ManualMacroTrigger : IMacroTrigger
{
    /// <summary>The single shared instance.</summary>
    public static readonly ManualMacroTrigger Instance = new();

    private ManualMacroTrigger() { }

    /// <inheritdoc />
    public TriggerKind Kind => TriggerKind.Manual;

    /// <inheritdoc />
    public string Name => "Manual";

    /// <inheritdoc />
    public string? CommandName => null;

    /// <inheritdoc />
    public DateTimeOffset FiredAt => DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> Payload { get; } = new Dictionary<string, object?>();

    /// <inheritdoc />
    public bool IsManual => true;

    /// <inheritdoc />
    public void CancelCommand() { /* no-op */ }

    /// <inheritdoc />
    public bool CommandCancelled => false;
}
