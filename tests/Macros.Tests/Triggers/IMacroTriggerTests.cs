using System;
using System.Collections.Generic;
using Macros.Engine.Triggers;
using Xunit;

namespace Macros.Tests.Triggers;

/// <summary>
/// Contract tests for the <see cref="IMacroTrigger"/> hierarchy introduced in
/// <c>m4-imacrotrigger-globals</c>: ManualMacroTrigger, VsEventMacroTrigger, and
/// CommandMacroTrigger.
/// </summary>
public sealed class IMacroTriggerTests
{
    // -----------------------------------------------------------------------
    // ManualMacroTrigger
    // -----------------------------------------------------------------------

    [Fact]
    public void ManualMacroTrigger_HasExpectedShape()
    {
        IMacroTrigger trigger = ManualMacroTrigger.Instance;

        Assert.Equal(TriggerKind.Manual, trigger.Kind);
        Assert.Equal("Manual", trigger.Name);
        Assert.Null(trigger.CommandName);
        Assert.True(trigger.IsManual);
        Assert.False(trigger.CommandCancelled);
        Assert.Empty(trigger.Payload);
    }

    [Fact]
    public void ManualMacroTrigger_CancelCommand_IsNoOp()
    {
        IMacroTrigger trigger = ManualMacroTrigger.Instance;

        trigger.CancelCommand(); // must not throw

        Assert.False(trigger.CommandCancelled);
    }

    [Fact]
    public void ManualMacroTrigger_IsSingleton()
    {
        Assert.Same(ManualMacroTrigger.Instance, ManualMacroTrigger.Instance);
    }

    // -----------------------------------------------------------------------
    // VsEventMacroTrigger
    // -----------------------------------------------------------------------

    [Fact]
    public void VsEventMacroTrigger_CapturesFields()
    {
        var firedAt = new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var payload = new Dictionary<string, object?> { ["Success"] = true };

        var trigger = new VsEventMacroTrigger("Build.SolutionBuildDone", firedAt, payload);

        Assert.Equal(TriggerKind.VsEvent, trigger.Kind);
        Assert.Equal("Build.SolutionBuildDone", trigger.Name);
        Assert.Null(trigger.CommandName);
        Assert.Equal(firedAt, trigger.FiredAt);
        Assert.Same(payload, trigger.Payload);
        Assert.False(trigger.IsManual);
        Assert.False(trigger.CommandCancelled);
    }

    [Fact]
    public void VsEventMacroTrigger_CancelCommand_IsNoOp()
    {
        var trigger = new VsEventMacroTrigger("Build.SolutionBuildDone", DateTimeOffset.UtcNow,
            new Dictionary<string, object?>());

        trigger.CancelCommand(); // must not throw

        Assert.False(trigger.CommandCancelled);
    }

    // -----------------------------------------------------------------------
    // CommandMacroTrigger — BeforeCommand
    // -----------------------------------------------------------------------

    [Fact]
    public void CommandMacroTrigger_BeforeCommand_HasExpectedShape()
    {
        var firedAt = DateTimeOffset.UtcNow;
        var trigger = new CommandMacroTrigger(TriggerKind.BeforeCommand, "File.Save", firedAt);

        Assert.Equal(TriggerKind.BeforeCommand, trigger.Kind);
        Assert.Equal("File.Save", trigger.Name);
        Assert.Equal("File.Save", trigger.CommandName);
        Assert.Equal(firedAt, trigger.FiredAt);
        Assert.False(trigger.IsManual);
        Assert.False(trigger.CommandCancelled);
        Assert.Empty(trigger.Payload);
    }

    [Fact]
    public void CommandMacroTrigger_BeforeCommand_CancelCommand_SetsCommandCancelled()
    {
        var trigger = new CommandMacroTrigger(TriggerKind.BeforeCommand, "File.Save", DateTimeOffset.UtcNow);

        Assert.False(trigger.CommandCancelled);
        trigger.CancelCommand();
        Assert.True(trigger.CommandCancelled);
    }

    [Fact]
    public void CommandMacroTrigger_BeforeCommand_CancelCommand_IsIdempotent()
    {
        var trigger = new CommandMacroTrigger(TriggerKind.BeforeCommand, "File.Save", DateTimeOffset.UtcNow);

        trigger.CancelCommand();
        trigger.CancelCommand(); // must not throw

        Assert.True(trigger.CommandCancelled);
    }

    // -----------------------------------------------------------------------
    // CommandMacroTrigger — AfterCommand
    // -----------------------------------------------------------------------

    [Fact]
    public void CommandMacroTrigger_AfterCommand_HasExpectedShape()
    {
        var trigger = new CommandMacroTrigger(TriggerKind.AfterCommand, "Build.BuildSolution", DateTimeOffset.UtcNow);

        Assert.Equal(TriggerKind.AfterCommand, trigger.Kind);
        Assert.Equal("Build.BuildSolution", trigger.CommandName);
        Assert.False(trigger.IsManual);
        Assert.False(trigger.CommandCancelled);
    }

    [Fact]
    public void CommandMacroTrigger_AfterCommand_CancelCommand_IsNoOp()
    {
        var trigger = new CommandMacroTrigger(TriggerKind.AfterCommand, "Build.BuildSolution", DateTimeOffset.UtcNow);

        trigger.CancelCommand(); // already fired — must be no-op

        Assert.False(trigger.CommandCancelled);
    }

    // -----------------------------------------------------------------------
    // CommandMacroTrigger — invalid kind
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(TriggerKind.Manual)]
    [InlineData(TriggerKind.VsEvent)]
    public void CommandMacroTrigger_InvalidKind_Throws(TriggerKind invalidKind)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new CommandMacroTrigger(invalidKind, "Some.Command", DateTimeOffset.UtcNow));

        Assert.Equal("kind", ex.ParamName);
    }

    // -----------------------------------------------------------------------
    // CommandMacroTrigger — custom payload
    // -----------------------------------------------------------------------

    [Fact]
    public void CommandMacroTrigger_WithPayload_ExposesPayload()
    {
        var payload = new Dictionary<string, object?> { ["Extra"] = 42 };
        var trigger = new CommandMacroTrigger(TriggerKind.BeforeCommand, "File.Save", DateTimeOffset.UtcNow, payload);

        Assert.Same(payload, trigger.Payload);
    }
}
