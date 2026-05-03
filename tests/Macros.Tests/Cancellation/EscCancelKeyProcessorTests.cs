using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine;
using Macros.Engine.Cancellation;
using Macros.Engine.Player;
using Macros.Engine.Recording;
using Xunit;

namespace Macros.Tests.Cancellation;

/// <summary>
/// Verifies the core Esc-cancel decision in <see cref="EscCancelCore.TryHandleEscapeKey"/>.
/// Tests are pure engine-layer: no WPF or VS shell required.
/// </summary>
public sealed class EscCancelKeyProcessorTests
{
    [Fact]
    public void TryHandleEscapeKey_WhenPlaying_CancelsServiceAndReturnsTrue()
    {
        var service = new FakeMacroService(MacroState.Playing);

        bool handled = EscCancelCore.TryHandleEscapeKey(service);

        Assert.True(handled);
        Assert.Equal(1, service.CancelActivePlayCallCount);
    }

    [Fact]
    public void TryHandleEscapeKey_WhenIdle_DoesNotCancelAndReturnsFalse()
    {
        var service = new FakeMacroService(MacroState.Idle);

        bool handled = EscCancelCore.TryHandleEscapeKey(service);

        Assert.False(handled);
        Assert.Equal(0, service.CancelActivePlayCallCount);
    }

    [Fact]
    public void TryHandleEscapeKey_WhenRecording_DoesNotCancelAndReturnsFalse()
    {
        var service = new FakeMacroService(MacroState.Recording);

        bool handled = EscCancelCore.TryHandleEscapeKey(service);

        Assert.False(handled);
        Assert.Equal(0, service.CancelActivePlayCallCount);
    }

    [Fact]
    public void TryHandleEscapeKey_NullService_ReturnsFalse()
    {
        bool handled = EscCancelCore.TryHandleEscapeKey(null);

        Assert.False(handled);
    }

    // ---------------------------------------------------------------------------
    // Fake IMacroService — records calls to CancelActivePlay.
    // ---------------------------------------------------------------------------

    private sealed class FakeMacroService : IMacroService
    {
        public FakeMacroService(MacroState state) => State = state;

        public MacroState State { get; }
        public int CancelActivePlayCallCount { get; private set; }

        public IRecordingSink? CurrentSession => null;
        public string? CurrentMacroSource => null;
        public string? CurrentMacroName => null;
        public string? CurrentMacroPath => null;
        public int CurrentRecordingMaxSteps => int.MaxValue;

        public event EventHandler<MacroStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler? RecordingCapReached { add { } remove { } }
        public event EventHandler<RecordingSavedEventArgs>? RecordingSaved { add { } remove { } }
        public event EventHandler<int>? RecordingStepCountChanged { add { } remove { } }
        public event EventHandler<TriggeredExecutionEventArgs>? TriggeredExecutionStarted { add { } remove { } }
        public event EventHandler<TriggeredExecutionEventArgs>? TriggeredExecutionEnded { add { } remove { } }

        public void CancelActivePlay() => CancelActivePlayCallCount++;

        public Task StartRecordingAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> StopRecordingAsync(CancellationToken ct = default) => Task.FromResult(string.Empty);
        public Task<MacroPlayResult> PlayCurrentAsync(CancellationToken ct = default)
            => Task.FromResult(new MacroPlayResult(false, null, null, TimeSpan.Zero));
        public Task<MacroPlayResult> PlayByNameAsync(string name, Macros.Engine.Storage.MacroScope scope, CancellationToken cancellation = default)
            => Task.FromResult(new MacroPlayResult(false, null, null, TimeSpan.Zero));
        public Task CancelAsync() => Task.CompletedTask;
    }
}
