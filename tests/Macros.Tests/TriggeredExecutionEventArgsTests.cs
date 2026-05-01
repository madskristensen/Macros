using System;
using Macros.Engine;
using Macros.Engine.Triggers;
using Xunit;

namespace Macros.Tests;

public sealed class TriggeredExecutionEventArgsTests
{
    [Fact]
    public void Constructor_WithValidArgs_StoresAll()
    {
        var args = new TriggeredExecutionEventArgs("TestMacro", "BeforeTest", TriggerKind.BeforeCommand);

        Assert.Equal("TestMacro", args.MacroName);
        Assert.Equal("BeforeTest", args.TriggerName);
        Assert.Equal(TriggerKind.BeforeCommand, args.Kind);
    }

    [Fact]
    public void Constructor_WithNullMacroName_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new TriggeredExecutionEventArgs(null!, "trigger", TriggerKind.BeforeCommand));
        Assert.Contains("non-empty", ex.Message);
    }

    [Fact]
    public void Constructor_WithWhitespaceMacroName_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new TriggeredExecutionEventArgs("   ", "trigger", TriggerKind.BeforeCommand));
        Assert.Contains("non-empty", ex.Message);
    }

    [Fact]
    public void Constructor_WithNullTriggerName_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new TriggeredExecutionEventArgs("macro", null!, TriggerKind.BeforeCommand));
        Assert.Contains("non-empty", ex.Message);
    }

    [Fact]
    public void Constructor_WithWhitespaceTriggerName_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new TriggeredExecutionEventArgs("macro", "   ", TriggerKind.BeforeCommand));
        Assert.Contains("non-empty", ex.Message);
    }

    [Theory]
    [InlineData(TriggerKind.BeforeCommand)]
    [InlineData(TriggerKind.AfterCommand)]
    [InlineData(TriggerKind.VsEvent)]
    [InlineData(TriggerKind.Manual)]
    public void Constructor_WithVariousTriggerKinds_Stores(TriggerKind kind)
    {
        var args = new TriggeredExecutionEventArgs("TestMacro", "trigger", kind);
        Assert.Equal(kind, args.Kind);
    }
}
