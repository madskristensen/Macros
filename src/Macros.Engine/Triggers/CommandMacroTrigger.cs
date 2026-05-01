using System;
using System.Collections.Generic;

namespace Macros.Engine.Triggers;

/// <summary>
/// <see cref="IMacroTrigger"/> produced for <see cref="TriggerKind.BeforeCommand"/> and
/// <see cref="TriggerKind.AfterCommand"/> triggers. Only BeforeCommand supports command
/// cancellation; AfterCommand ignores <see cref="CancelCommand"/> calls.
/// </summary>
internal sealed class CommandMacroTrigger : IMacroTrigger
{
    private bool _cancelled;

    /// <inheritdoc />
    public TriggerKind Kind { get; }

    /// <inheritdoc />
    public string Name => CommandName ?? "";

    /// <inheritdoc />
    public string? CommandName { get; }

    /// <inheritdoc />
    public DateTimeOffset FiredAt { get; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> Payload { get; }

    /// <inheritdoc />
    public bool IsManual => false;

    /// <inheritdoc />
    public bool CommandCancelled => _cancelled;

    public CommandMacroTrigger(
        TriggerKind kind,
        string commandName,
        DateTimeOffset firedAt,
        IReadOnlyDictionary<string, object?>? payload = null)
    {
        if (kind != TriggerKind.BeforeCommand && kind != TriggerKind.AfterCommand)
            throw new ArgumentException("Only BeforeCommand or AfterCommand kinds are valid.", nameof(kind));

        Kind = kind;
        CommandName = commandName ?? throw new ArgumentNullException(nameof(commandName));
        FiredAt = firedAt;
        Payload = payload ?? new Dictionary<string, object?>();
    }

    /// <inheritdoc />
    public void CancelCommand()
    {
        if (Kind == TriggerKind.BeforeCommand)
            _cancelled = true;
        // AfterCommand: already fired, cancellation has no effect.
    }
}
