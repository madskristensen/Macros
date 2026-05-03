using System;
using System.Globalization;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;

namespace Macros.Engine.Scripting;

/// <summary>
/// Structured logging helpers available to <c>.csx</c> macros. Routes messages to the
/// shared <b>Macros</b> Output pane — the same channel <c>MacroErrorRenderer</c> uses for
/// playback failures — so users have a single place to read script output.
/// </summary>
/// <remarks>
/// <para>
/// <b>Format.</b> Each log line is prefixed with a local time stamp, the severity, and the
/// macro name resolved from the ambient <see cref="MacroGlobals"/>: <c>[HH:mm:ss INFO  Name] message</c>.
/// The fixed-width severity column keeps successive lines aligned in the Output pane.
/// </para>
/// <para>
/// <b>Threading.</b> Every method awaits its way to the UI thread inside the toolkit's
/// <see cref="OutputWindowPane.WriteLineAsync(string)"/>; callers can await without worrying
/// about which thread they started on.
/// </para>
/// <para>
/// <b>No fire-and-forget.</b> All methods are <see langword="async"/> so callers must
/// <see langword="await"/> them. This avoids the classic "log line was lost because the
/// task wasn't observed" footgun and keeps macro execution deterministic.
/// </para>
/// </remarks>
public static class Log
{
    private const string OutputPaneName = "Macros";

    /// <summary>Writes an informational message to the Macros Output pane.</summary>
    /// <param name="message">The text to log. <see langword="null"/> is logged as the literal string <c>"&lt;null&gt;"</c>.</param>
    public static Task InfoAsync(string message) => WriteAsync("INFO ", message);

    /// <summary>Writes a warning message to the Macros Output pane.</summary>
    /// <param name="message">The text to log. <see langword="null"/> is logged as the literal string <c>"&lt;null&gt;"</c>.</param>
    public static Task WarnAsync(string message) => WriteAsync("WARN ", message);

    /// <summary>Writes an error message to the Macros Output pane.</summary>
    /// <param name="message">The text to log. <see langword="null"/> is logged as the literal string <c>"&lt;null&gt;"</c>.</param>
    public static Task ErrorAsync(string message) => WriteAsync("ERROR", message);

    /// <summary>
    /// Pure formatter exposed for unit tests. Produces the exact line that would be written
    /// to the Output pane (without the trailing newline that <see cref="OutputWindowPane.WriteLineAsync(string)"/> adds).
    /// </summary>
    /// <param name="timestamp">Local time stamp to use; tests pass a fixed value for determinism.</param>
    /// <param name="severity">Five-character severity tag (e.g. <c>"INFO "</c>).</param>
    /// <param name="macroName">Macro name surfaced via <see cref="IMacroContext.MacroName"/>; <see langword="null"/> renders as <c>"&lt;unknown&gt;"</c>.</param>
    /// <param name="message">The text to log.</param>
    /// <returns>The formatted line, without a trailing newline.</returns>
    internal static string Format(DateTime timestamp, string severity, string? macroName, string? message)
    {
        string time = timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        string sev = severity ?? "INFO ";
        string source = string.IsNullOrEmpty(macroName) ? "<unknown>" : macroName!;
        string body = message ?? "<null>";
        return $"[{time} {sev} {source}] {body}";
    }

    private static async Task WriteAsync(string severity, string message)
    {
        // Resolve the macro name from the ambient context if available. Logging from outside
        // a macro invocation (e.g. an integration test that calls Log.InfoAsync directly)
        // shouldn't fail — fall back to "<unknown>".
        string? macroName = Helpers.CurrentGlobals.Value?.Context.MacroName;
        string line = Format(DateTime.Now, severity, macroName, message);

        OutputWindowPane pane = await VS.Windows.CreateOutputWindowPaneAsync(OutputPaneName, lazyCreate: false);
        await pane.WriteLineAsync(line);
    }
}
