namespace Macros.Engine.Triggers;

/// <summary>
/// Discriminator for a <see cref="TriggerBinding"/>. Mirrors the four <c>@trigger</c>
/// directive forms supported in the leading comment block of a <c>.csx</c> macro
/// (see <see cref="TriggerDirectiveParser"/>).
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><see cref="Manual"/> — invoked only via UI (toolbar, hotkey, palette, tool window).
///         The default when no <c>@trigger</c> directive is present.</item>
///   <item><see cref="VsEvent"/> — bound to a <c>Community.VisualStudio.Toolkit.VS.Events</c>
///         handler discovered by reflection (e.g. <c>Build.SolutionBuildDone</c>).</item>
///   <item><see cref="BeforeCommand"/> — runs synchronously before a named VS command and may
///         cancel it via <c>Trigger.CancelCommand()</c>.</item>
///   <item><see cref="AfterCommand"/> — queued to run after a named VS command has dispatched.</item>
/// </list>
/// </remarks>
public enum TriggerKind
{
    Manual,
    VsEvent,
    BeforeCommand,
    AfterCommand,
}
