using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Macros.Engine.Triggers;

/// <summary>
/// Pure-string parser for the <c>@trigger</c> directives that may appear in the leading
/// comment block of a <c>.csx</c> macro. No Visual Studio dependencies — fully unit-testable.
/// </summary>
/// <remarks>
/// <para>
/// Recognized directive forms (one per <c>// @trigger ...</c> line, case-insensitive on the
/// keywords <c>@trigger</c>, <c>Manual</c>, <c>BeforeCommand</c>, <c>AfterCommand</c>, and
/// <c>when</c>/<c>and</c>):
/// </para>
/// <list type="bullet">
///   <item><c>Manual</c> — explicit Manual binding (same as omitting all directives).</item>
///   <item><c>Category.EventName</c> — a <see cref="TriggerKind.VsEvent"/> binding.</item>
///   <item><c>Category.EventName when key=val [and key2=val2]</c> — VsEvent with filters.</item>
///   <item><c>BeforeCommand &lt;command-name&gt;</c> — synchronous, can cancel.</item>
///   <item><c>AfterCommand &lt;command-name&gt;</c> — queued, post-dispatch.</item>
/// </list>
/// <para>
/// Parsing scans line-by-line from the top of the file, skipping blank lines and consuming
/// any line whose first non-whitespace characters are <c>//</c>. The first non-comment,
/// non-blank line (typically <c>using ...</c> or <c>#r</c>) terminates the scan — this is
/// the "leading comment block" the recorder emits and that <c>CSharpCodeGenerator</c>
/// preserves on round-trip.
/// </para>
/// <para>
/// If the source contains no recognizable directive (empty file, no comment block, or only
/// non-<c>@trigger</c> comments), the result is a single-element list containing
/// <see cref="TriggerBinding.Manual"/>. This makes Manual the default everywhere — callers
/// don't need null/empty guards.
/// </para>
/// <para>
/// Threading: this method is pure; it may be called from any thread (see the threading
/// table in <c>danny-triggers-and-scope.md §10</c>).
/// </para>
/// </remarks>
public static class TriggerDirectiveParser
{
    private const string DirectiveKeyword = "@trigger";
    private const string BeforeCommandPrefix = "BeforeCommand ";
    private const string AfterCommandPrefix = "AfterCommand ";
    private const string WhenSeparator = " when ";

    private static readonly Regex AndSplitter =
        new(@"\s+and\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Parses the leading comment block of <paramref name="source"/> and returns every
    /// <c>@trigger</c> binding found, in declared order. Returns
    /// <c>[<see cref="TriggerBinding.Manual"/>]</c> if none are found.
    /// </summary>
    public static IReadOnlyList<TriggerBinding> Parse(string? source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return new[] { TriggerBinding.Manual };
        }

        var bindings = new List<TriggerBinding>();
        using var reader = new StringReader(source);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                var binding = TryParseDirective(trimmed);
                if (binding != null)
                {
                    bindings.Add(binding);
                }
                continue;
            }

            // First non-blank, non-comment line — the comment block has ended.
            break;
        }

        return bindings.Count == 0
            ? new[] { TriggerBinding.Manual }
            : bindings.ToArray();
    }

    private static TriggerBinding? TryParseDirective(string line)
    {
        // Strip the leading "//" and any whitespace that follows.
        var content = line.Substring(2).TrimStart();
        if (!content.StartsWith(DirectiveKeyword, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Guard against "@triggered" / "@triggers" typos: the keyword must be followed by
        // whitespace (or end-of-line, which is rejected below as an empty payload).
        var afterKeyword = content.Substring(DirectiveKeyword.Length);
        if (afterKeyword.Length > 0 && !char.IsWhiteSpace(afterKeyword[0]))
        {
            return null;
        }

        var rest = afterKeyword.TrimStart().TrimEnd();
        if (rest.Length == 0)
        {
            return null;
        }

        if (rest.Equals("Manual", StringComparison.OrdinalIgnoreCase))
        {
            return TriggerBinding.Manual;
        }

        if (rest.StartsWith(BeforeCommandPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = rest.Substring(BeforeCommandPrefix.Length).Trim();
            return string.IsNullOrEmpty(name)
                ? null
                : new TriggerBinding(TriggerKind.BeforeCommand, name);
        }

        if (rest.StartsWith(AfterCommandPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = rest.Substring(AfterCommandPrefix.Length).Trim();
            return string.IsNullOrEmpty(name)
                ? null
                : new TriggerBinding(TriggerKind.AfterCommand, name);
        }

        // VsEvent form — possibly with " when key=val [and ...]" filters.
        string eventName;
        IReadOnlyDictionary<string, string> filters;
        var whenIdx = rest.IndexOf(WhenSeparator, StringComparison.OrdinalIgnoreCase);
        if (whenIdx < 0)
        {
            eventName = rest;
            filters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            eventName = rest.Substring(0, whenIdx).Trim();
            var filterStr = rest.Substring(whenIdx + WhenSeparator.Length).Trim();
            filters = ParseFilters(filterStr);
        }

        if (string.IsNullOrEmpty(eventName) || !eventName.Contains('.'))
        {
            return null;
        }

        return new TriggerBinding(TriggerKind.VsEvent, eventName, filters);
    }

    private static IReadOnlyDictionary<string, string> ParseFilters(string s)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (s.Length == 0)
        {
            return dict;
        }

        foreach (var part in AndSplitter.Split(s))
        {
            var p = part.Trim();
            if (p.Length == 0)
            {
                continue;
            }

            var eq = p.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = p.Substring(0, eq).Trim();
            var val = p.Substring(eq + 1).Trim();
            if (key.Length == 0)
            {
                continue;
            }

            dict[key] = val;
        }

        return dict;
    }

    /// <summary>
    /// Renders a list of bindings back to canonical <c>// @trigger ...</c> comment lines
    /// (one per binding, terminated with the platform newline). Used by the codegen path
    /// to round-trip triggers through Save / Edit / Save without losing user formatting.
    /// </summary>
    public static string Render(IReadOnlyList<TriggerBinding> bindings)
    {
        if (bindings == null) throw new ArgumentNullException(nameof(bindings));

        var sb = new StringBuilder();
        foreach (var b in bindings)
        {
            sb.Append("// @trigger ");
            switch (b.Kind)
            {
                case TriggerKind.Manual:
                    sb.AppendLine("Manual");
                    break;
                case TriggerKind.BeforeCommand:
                    sb.Append("BeforeCommand ").AppendLine(b.Name);
                    break;
                case TriggerKind.AfterCommand:
                    sb.Append("AfterCommand ").AppendLine(b.Name);
                    break;
                case TriggerKind.VsEvent:
                    sb.Append(b.Name);
                    if (b.Filters.Count > 0)
                    {
                        sb.Append(" when ");
                        sb.Append(string.Join(
                            " and ",
                            b.Filters.Select(kv => $"{kv.Key}={kv.Value}")));
                    }
                    sb.AppendLine();
                    break;
            }
        }
        return sb.ToString();
    }
}
