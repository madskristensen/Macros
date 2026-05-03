using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Macros.Commands;
using Macros.Engine;
using Moq;
using Xunit;

namespace Macros.Tests.Commands;

public sealed class RecordingCommandUtilitiesTests
{
    [Fact]
    public async Task StopRecordingAndOpenAsync_OpensSavedRecording()
    {
        string path = Path.Combine(Path.GetTempPath(), "Macros", "Recorded.csx");
        var service = CreateService(path, raiseSavedEvent: true, out var getHandler);
        string? openedPath = null;

        await RecordingCommandUtilities.StopRecordingAndOpenAsync(
            service.Object,
            p =>
            {
                openedPath = p;
                return Task.CompletedTask;
            },
            TimeSpan.FromMilliseconds(1));

        Assert.Equal(path, openedPath);
        Assert.Null(getHandler());
    }

    [Fact]
    public async Task StopRecordingAndOpenAsync_Timeout_DoesNotOpenDocument()
    {
        var service = CreateService(Path.Combine(Path.GetTempPath(), "Macros", "Recorded.csx"), raiseSavedEvent: false, out var getHandler);
        bool opened = false;

        await RecordingCommandUtilities.StopRecordingAndOpenAsync(
            service.Object,
            _ =>
            {
                opened = true;
                return Task.CompletedTask;
            },
            TimeSpan.FromMilliseconds(1));

        Assert.False(opened);
        Assert.Null(getHandler());
    }

    [Fact]
    public async Task StopRecordingAndOpenAsync_OpenFailure_IsSwallowedAndUnsubscribes()
    {
        var service = CreateService(Path.Combine(Path.GetTempPath(), "Macros", "Recorded.csx"), raiseSavedEvent: true, out var getHandler);

        await RecordingCommandUtilities.StopRecordingAndOpenAsync(
            service.Object,
            _ => throw new InvalidOperationException("open failed"),
            TimeSpan.FromMilliseconds(1));

        Assert.Null(getHandler());
    }

    private static Mock<IMacroService> CreateService(
        string savedPath,
        bool raiseSavedEvent,
        out Func<EventHandler<RecordingSavedEventArgs>?> getHandler)
    {
        EventHandler<RecordingSavedEventArgs>? handler = null;
        getHandler = () => handler;

        var service = new Mock<IMacroService>(MockBehavior.Strict);
        service.SetupAdd(s => s.RecordingSaved += It.IsAny<EventHandler<RecordingSavedEventArgs>>())
            .Callback<EventHandler<RecordingSavedEventArgs>>(h => handler += h);
        service.SetupRemove(s => s.RecordingSaved -= It.IsAny<EventHandler<RecordingSavedEventArgs>>())
            .Callback<EventHandler<RecordingSavedEventArgs>>(h => handler -= h);
        service.Setup(s => s.StopRecordingAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (raiseSavedEvent)
                {
                    handler?.Invoke(service.Object, new RecordingSavedEventArgs(savedPath));
                }

                // StopRecordingAndOpenAsync only needs the RecordingSaved event path; the generated source is ignored.
                return Task.FromResult(string.Empty);
            });

        return service;
    }
}
