using System;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Macros.Engine;
using Task = System.Threading.Tasks.Task;

namespace Macros.Commands;

internal static class RecordingCommandUtilities
{
    public static async Task StopRecordingAndOpenAsync(
        IMacroService service,
        Func<string, Task>? openDocumentAsync = null,
        TimeSpan? saveTimeout = null)
    {
        openDocumentAsync ??= VS.Documents.OpenAsync;

        var tcs = new TaskCompletionSource<string>();
        EventHandler<RecordingSavedEventArgs> handler = (_, args) => tcs.TrySetResult(args.Path);
        service.RecordingSaved += handler;

        try
        {
            await service.StopRecordingAsync();

            var delayTask = Task.Delay(saveTimeout ?? TimeSpan.FromSeconds(5));
            var winner = await Task.WhenAny(tcs.Task, delayTask);
            if (winner == tcs.Task)
            {
                try
                {
                    await openDocumentAsync(await tcs.Task);
                }
                catch (Exception ex)
                {
                    // Opening can fail if the path becomes invalid between save and open.
                    // Log but don't surface — the macro is already persisted.
                    await ex.LogAsync();
                }
            }
        }
        finally
        {
            service.RecordingSaved -= handler;
        }
    }
}
