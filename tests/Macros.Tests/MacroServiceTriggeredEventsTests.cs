using System.Collections.Generic;
using Macros.Engine;
using Macros.Engine.Triggers;
using Microsoft.VisualStudio.Threading;
using Xunit;

namespace Macros.Tests;

public sealed class MacroServiceTriggeredEventsTests
{
    private static MacroService CreateService()
    {
#pragma warning disable VSSDK005
        var ctx = new JoinableTaskContext();
#pragma warning restore VSSDK005
        return new MacroService(ctx.Factory);
    }

    [Fact]
    public void RaiseTriggeredStarted_FiresEvent()
    {
        var svc = CreateService();
        var events = new List<TriggeredExecutionEventArgs>();
        svc.TriggeredExecutionStarted += (_, e) => events.Add(e);

        var args = new TriggeredExecutionEventArgs("TestMacro", "command", TriggerKind.BeforeCommand);
        svc.RaiseTriggeredStarted(args);

        var evt = Assert.Single(events);
        Assert.Equal("TestMacro", evt.MacroName);
        Assert.Equal("command", evt.TriggerName);
        Assert.Equal(TriggerKind.BeforeCommand, evt.Kind);
    }

    [Fact]
    public void RaiseTriggeredEnded_FiresEvent()
    {
        var svc = CreateService();
        var events = new List<TriggeredExecutionEventArgs>();
        svc.TriggeredExecutionEnded += (_, e) => events.Add(e);

        var args = new TriggeredExecutionEventArgs("TestMacro", "command", TriggerKind.AfterCommand);
        svc.RaiseTriggeredEnded(args);

        var evt = Assert.Single(events);
        Assert.Equal("TestMacro", evt.MacroName);
        Assert.Equal("command", evt.TriggerName);
        Assert.Equal(TriggerKind.AfterCommand, evt.Kind);
    }
}
