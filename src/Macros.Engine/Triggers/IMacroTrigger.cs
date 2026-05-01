using System;
using System.Collections.Generic;

namespace Macros.Engine.Triggers;

/// <summary>
/// A typed, immutable snapshot of the event that caused a macro to run. Script authors
/// receive this as <c>Trigger</c> (via <see cref="Macros.Engine.Scripting.MacroGlobals"/>)
/// and can branch on <see cref="Kind"/>, <see cref="CommandName"/>, or <see cref="Payload"/>
/// depending on the scenario.
/// </summary>
public interface IMacroTrigger
{
    /// <summary>Gets the discriminator: Manual, VsEvent, BeforeCommand, or AfterCommand.</summary>
    TriggerKind Kind { get; }

    /// <summary>
    /// Gets the canonical name. <c>"Manual"</c> for manual invocations; the VS event path
    /// (e.g. <c>"Build.SolutionBuildDone"</c>) for <see cref="TriggerKind.VsEvent"/>; the
    /// command name (e.g. <c>"File.Save"</c>) for Before/AfterCommand.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the VS command name for <see cref="TriggerKind.BeforeCommand"/> and
    /// <see cref="TriggerKind.AfterCommand"/> triggers; <see langword="null"/> for all other kinds.
    /// </summary>
    string? CommandName { get; }

    /// <summary>Gets the UTC timestamp at which the trigger fired.</summary>
    DateTimeOffset FiredAt { get; }

    /// <summary>
    /// Gets structured event-argument fields for <see cref="TriggerKind.VsEvent"/> triggers.
    /// Always empty for Manual and command triggers.
    /// </summary>
    IReadOnlyDictionary<string, object?> Payload { get; }

    /// <summary>Gets a value indicating whether this is a manual (user-initiated) invocation.</summary>
    bool IsManual { get; }

    /// <summary>
    /// For <see cref="TriggerKind.BeforeCommand"/> triggers: requests that the underlying VS
    /// command be suppressed. No-op for all other kinds.
    /// </summary>
    void CancelCommand();

    /// <summary>Gets a value indicating whether <see cref="CancelCommand"/> has been called.</summary>
    bool CommandCancelled { get; }
}
