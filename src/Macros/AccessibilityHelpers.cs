using System;
using System.Globalization;
using Macros.Engine.Storage;

namespace Macros;

/// <summary>
/// Pure-string helpers used by the WPF accessibility surfaces (tool window rows, dialog
/// titles, etc.) so screen readers can announce a richer label than the visible text alone.
/// </summary>
/// <remarks>
/// Lives outside <c>Macros.ToolWindows</c> so the dialog code can also reach it without a
/// circular dependency. Every method is deterministic and culture-neutral so the unit tests
/// can pin the format without spinning up a WPF host.
/// </remarks>
public static class AccessibilityHelpers
{
    /// <summary>
    /// Composite-format string used by <see cref="FormatMacroAutomationName"/> so the XAML
    /// can mirror the format via a <c>MultiBinding StringFormat</c> if it ever needs to.
    /// </summary>
    public const string MacroAutomationNameFormat = "Macro {0}, {1}, modified {2}";

    /// <summary>
    /// Builds the announcement string read by Narrator/JAWS when focus enters a macro row in
    /// the tool window's grouped <c>ListView</c>.
    /// </summary>
    /// <param name="entry">The descriptor backing the row. Required.</param>
    /// <param name="triggersSummary">
    /// Compact trigger summary as rendered in the "Triggers" column (e.g. <c>"Manual"</c>,
    /// <c>"VS:BuildBegin +1"</c>). Empty / whitespace becomes <c>"no triggers"</c> so the
    /// announcement still parses as a sentence.
    /// </param>
    /// <param name="modifiedRelative">
    /// Short relative timestamp as rendered in the "Modified" column (e.g. <c>"5m"</c>,
    /// <c>"yesterday"</c>, <c>"2024-08-30"</c>).
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is <see langword="null"/>.</exception>
    public static string FormatMacroAutomationName(MacroEntry entry, string? triggersSummary, string? modifiedRelative)
    {
        if (entry is null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        string triggers = string.IsNullOrWhiteSpace(triggersSummary) ? "no triggers" : triggersSummary!;
        string modified = string.IsNullOrWhiteSpace(modifiedRelative) ? "unknown date" : modifiedRelative!;

        return string.Format(
            CultureInfo.CurrentCulture,
            MacroAutomationNameFormat,
            entry.Name,
            triggers,
            modified);
    }
}
