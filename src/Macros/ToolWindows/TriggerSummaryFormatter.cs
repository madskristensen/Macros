using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Macros.Engine.Triggers;

namespace Macros.ToolWindows;

/// <summary>
/// Pure helpers that compress an arbitrary list of <see cref="TriggerBinding"/>s into the
/// short summary the tool window's "Triggers" column shows and the multi-line detail string
/// rendered as a tooltip on hover.
/// </summary>
/// <remarks>
/// <para>
/// The column is intentionally narrow — most rows have zero or one trigger and the
/// summary is a single short word (the bell glyph + count fallback only fires for 2+).
/// Keeping the formatter pure (no view-model dependency, no allocation of
/// <see cref="System.Globalization.CultureInfo"/>) makes it trivial to unit-test and reuse
/// from any future surface that needs to present trigger metadata (Solution Explorer
/// node tooltips, the keymap palette, etc.).
/// </para>
/// <para>
/// The "Manual only" case is treated identically to "no triggers": the macro can only
/// be run via the UI, so there is nothing event-bound worth highlighting in the column.
/// </para>
/// </remarks>
public static class TriggerSummaryFormatter
{
    /// <summary>The bell glyph prefix used when the summary collapses 2+ bindings into a count.</summary>
    public const string BellGlyph = "\U0001F514";

    /// <summary>
    /// The summary rendered for macros without any meaningful (non-Manual) triggers — the
    /// macro can only be run from the UI. The word "Manual" is more communicative than a
    /// blank dash and immediately tells the user how the macro is invoked.
    /// </summary>
    public const string ManualSummary = "Manual";

    /// <summary>
    /// Returns the short single-line summary suitable for the "Triggers" column cell.
    /// </summary>
    /// <param name="bindings">The macro's trigger bindings. <see langword="null"/> is treated as empty.</param>
    public static string Summary(IReadOnlyList<TriggerBinding>? bindings)
    {
        var meaningful = NonManual(bindings);
        if (meaningful.Count == 0)
        {
            return ManualSummary;
        }

        if (meaningful.Count == 1)
        {
            return DescribeShort(meaningful[0]);
        }

        return $"{BellGlyph} {meaningful.Count} triggers";
    }

    /// <summary>
    /// Returns a full multi-line description suitable for the column tooltip.
    /// </summary>
    /// <param name="bindings">The macro's trigger bindings. <see langword="null"/> is treated as empty.</param>
    public static string Detail(IReadOnlyList<TriggerBinding>? bindings)
    {
        if (bindings is null || bindings.Count == 0)
        {
            return "Manual — invoked from UI only.";
        }

        var meaningful = NonManual(bindings);
        if (meaningful.Count == 0)
        {
            return "Manual — invoked from UI only.";
        }

        var sb = new StringBuilder();
        for (int i = 0; i < meaningful.Count; i++)
        {
            if (i > 0)
            {
                sb.Append('\n');
            }

            sb.Append(DescribeLong(meaningful[i]));
        }

        return sb.ToString();
    }

    private static List<TriggerBinding> NonManual(IReadOnlyList<TriggerBinding>? bindings)
    {
        if (bindings is null)
        {
            return new List<TriggerBinding>();
        }

        var result = new List<TriggerBinding>(bindings.Count);
        foreach (var b in bindings)
        {
            if (b is null || b.Kind == TriggerKind.Manual)
            {
                continue;
            }

            result.Add(b);
        }

        return result;
    }

    private static string DescribeShort(TriggerBinding binding) => binding.Kind switch
    {
        // VS event names are dotted (e.g. "Build.SolutionBuildDone"); take the first
        // segment as the category — short enough for the narrow column.
        TriggerKind.VsEvent => FirstSegment(binding.Name),
        TriggerKind.BeforeCommand => $"Before {binding.Name}",
        TriggerKind.AfterCommand => $"After {binding.Name}",
        _ => binding.Name,
    };

    private static string DescribeLong(TriggerBinding binding)
    {
        string body = binding.Kind switch
        {
            TriggerKind.VsEvent => $"On {binding.Name}",
            TriggerKind.BeforeCommand => $"Before command {binding.Name}",
            TriggerKind.AfterCommand => $"After command {binding.Name}",
            _ => binding.Name,
        };

        if (binding.Filters is null || binding.Filters.Count == 0)
        {
            return body;
        }

        var sb = new StringBuilder(body);
        sb.Append(" when ");
        bool first = true;
        foreach (var kv in binding.Filters)
        {
            if (!first)
            {
                sb.Append(" and ");
            }

            sb.Append(kv.Key).Append('=').Append(kv.Value);
            first = false;
        }

        return sb.ToString();
    }

    private static string FirstSegment(string dotted)
    {
        if (string.IsNullOrEmpty(dotted))
        {
            return string.Empty;
        }

        int dot = dotted.IndexOf('.');
        return dot < 0 ? dotted : dotted.Substring(0, dot);
    }
}
