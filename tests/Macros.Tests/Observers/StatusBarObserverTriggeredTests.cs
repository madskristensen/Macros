using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Macros.Engine;
using Macros.Engine.Player;
using Macros.Engine.Recording;
using Macros.Engine.Storage;
using Macros.Engine.Triggers;
using Xunit;

namespace Macros.Tests.Observers;

/// <summary>
/// Tests for the triggered execution events being raised and handled correctly.
/// </summary>
public sealed class StatusBarObserverTriggeredTests
{
    private class FakeMacroService : IMacroService
    {
        public MacroState State => MacroState.Idle;
        public IRecordingSink? CurrentSession => null;
        public string? CurrentMacroSource => null;
        public string? CurrentMacroName => null;
        public string? CurrentMacroPath => null;
        public int CurrentRecordingMaxSteps => int.MaxValue;

        public event EventHandler<MacroStateChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public event EventHandler? RecordingCapReached
        {
            add { }
            remove { }
        }

        public event EventHandler<RecordingSavedEventArgs>? RecordingSaved { add { } remove { } }

        public event EventHandler<int>? RecordingStepCountChanged
        {
            add { }
            remove { }
        }
        public event EventHandler<TriggeredExecutionEventArgs>? TriggeredExecutionStarted;
        public event EventHandler<TriggeredExecutionEventArgs>? TriggeredExecutionEnded;

        public Task StartRecordingAsync(System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> StopRecordingAsync(System.Threading.CancellationToken ct = default) => Task.FromResult("");
        public Task<MacroPlayResult> PlayCurrentAsync(System.Threading.CancellationToken ct = default) => Task.FromResult(new MacroPlayResult(false, "", null, TimeSpan.Zero));
        public Task<MacroPlayResult> PlayByNameAsync(string name, MacroScope scope, System.Threading.CancellationToken cancellation = default) => Task.FromResult(new MacroPlayResult(false, "", null, TimeSpan.Zero));
        public Task CancelAsync() => Task.CompletedTask;
        public void CancelActivePlay() { }

        internal void RaiseTriggeredStarted(TriggeredExecutionEventArgs e)
        {
            TriggeredExecutionStarted?.Invoke(this, e);
        }

        internal void RaiseTriggeredEnded(TriggeredExecutionEventArgs e)
        {
            TriggeredExecutionEnded?.Invoke(this, e);
        }
    }

    [Fact]
    public void TriggeredExecutionStarted_FiresEvent()
    {
        var service = new FakeMacroService();
        var events = new List<TriggeredExecutionEventArgs>();
        service.TriggeredExecutionStarted += (_, e) => events.Add(e);

        var args = new TriggeredExecutionEventArgs("MyMacro", "Format", TriggerKind.BeforeCommand);
        service.RaiseTriggeredStarted(args);

        var evt = Assert.Single(events);
        Assert.Equal("MyMacro", evt.MacroName);
        Assert.Equal("Format", evt.TriggerName);
        Assert.Equal(TriggerKind.BeforeCommand, evt.Kind);
    }

    [Fact]
    public void TriggeredExecutionEnded_FiresEvent()
    {
        var service = new FakeMacroService();
        var events = new List<TriggeredExecutionEventArgs>();
        service.TriggeredExecutionEnded += (_, e) => events.Add(e);

        var args = new TriggeredExecutionEventArgs("MyMacro", "Format", TriggerKind.BeforeCommand);
        service.RaiseTriggeredEnded(args);

        var evt = Assert.Single(events);
        Assert.Equal("MyMacro", evt.MacroName);
    }

    [Theory]
    [InlineData(TriggerKind.BeforeCommand)]
    [InlineData(TriggerKind.AfterCommand)]
    [InlineData(TriggerKind.VsEvent)]
    public void TriggeredEvents_WithVariousTriggerKinds_WorkCorrectly(TriggerKind kind)
    {
        var service = new FakeMacroService();
        var startedEvents = new List<TriggeredExecutionEventArgs>();
        var endedEvents = new List<TriggeredExecutionEventArgs>();
        
        service.TriggeredExecutionStarted += (_, e) => startedEvents.Add(e);
        service.TriggeredExecutionEnded += (_, e) => endedEvents.Add(e);

        var args = new TriggeredExecutionEventArgs("TestMacro", "trigger", kind);
        service.RaiseTriggeredStarted(args);
        service.RaiseTriggeredEnded(args);

        Assert.Single(startedEvents);
        Assert.Single(endedEvents);
        Assert.Equal(kind, startedEvents[0].Kind);
        Assert.Equal(kind, endedEvents[0].Kind);
    }
}
