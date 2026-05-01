using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Macros.Engine.Triggers;

namespace Macros.Commands;

/// <summary>
/// Pure string transformation that replaces every <c>// @trigger ...</c> directive in the
/// leading comment block of a <c>.csx</c> macro with a fresh set rendered from
/// <see cref="TriggerDirectiveParser.Render"/>. The body of the file (everything from the
/// first non-comment, non-blank line to EOF) is preserved byte-for-byte.
/// </summary>
/// <remarks>
/// <para>
/// Used by the M4 "Manage Triggers..." dialog so the user can add / remove triggers without
/// hand-editing <c>// @trigger</c> syntax. Saving the dialog rewrites the header and
/// persists via <see cref="Macros.Engine.Storage.IMacroStore.SaveAsAsync"/> with
/// <c>overwrite: true</c> — the storage layer's atomic temp+swap handles crash safety.
/// </para>
/// <para>
/// Algorithm:
/// </para>
/// <list type="number">
///   <item>Detect the source's dominant line terminator (CRLF if any <c>\r\n</c> present,
///         else LF). Newly-emitted trigger lines use that terminator.</item>
///   <item>Split the source into lines while remembering each line's original terminator
///         and starting byte offset.</item>
///   <item>Find the first non-comment, non-blank line — its starting offset is the body
///         start. Everything from that offset to EOF is copied through unchanged.</item>
///   <item>Walk the header lines, dropping any that the parser would recognize as
///         <c>@trigger</c> directives. Surviving lines keep their original text + terminator.</item>
///   <item>Insert the rendered new directive lines immediately after the LAST surviving
///         comment line (so they sit at the end of the comment block, before any blank-line
///         separator). When the header has no comments, the new directives form the entire
///         header and a blank line is inserted before the body.</item>
/// </list>
/// <para>
/// Idempotent: <c>Rewrite(Rewrite(source, b), b) == Rewrite(source, b)</c> for any source +
/// binding list <c>b</c>.
/// </para>
/// <para>
/// Threading: pure; safe to call from any thread.
/// </para>
/// </remarks>
internal static class TriggerHeaderRewriter
{
    /// <summary>
    /// Returns a copy of <paramref name="source"/> whose leading comment block contains
    /// exactly the <c>@trigger</c> directives in <paramref name="bindings"/>, in declared
    /// order. The body (everything from the first non-comment, non-blank line to EOF) is
    /// preserved byte-for-byte.
    /// </summary>
    /// <param name="source">The full <c>.csx</c> source. <see langword="null"/> is treated as empty.</param>
    /// <param name="bindings">The new set of trigger bindings. May be empty (strips all directives).</param>
    /// <exception cref="ArgumentNullException"><paramref name="bindings"/> is <see langword="null"/>.</exception>
    public static string Rewrite(string? source, IReadOnlyList<TriggerBinding> bindings)
    {
        if (bindings is null) throw new ArgumentNullException(nameof(bindings));

        source ??= string.Empty;

        // Detect the dominant terminator. Files written by CSharpCodeGenerator use LF; files
        // edited in VS often pick up CRLF. Either way, picking one and using it for all newly
        // emitted lines keeps the file uniform.
        var newline = source.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";

        var lines = SplitLines(source);

        // Find the first non-comment, non-blank line — its start offset is where the body
        // begins. Everything from that offset onward is preserved verbatim.
        int headerEnd = lines.Count;
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (IsBlank(l.Content)) continue;
            if (IsComment(l.Content)) continue;
            headerEnd = i;
            break;
        }

        int bodyStartOffset = headerEnd < lines.Count ? lines[headerEnd].Start : source.Length;
        var body = source.Substring(bodyStartOffset);

        // Walk the header lines and drop @trigger directives.
        var surviving = new List<Line>(headerEnd);
        for (int i = 0; i < headerEnd; i++)
        {
            var l = lines[i];
            if (IsTriggerDirective(l.Content)) continue;
            surviving.Add(l);
        }

        // Build the new directive block.
        var directives = new List<Line>(bindings.Count);
        foreach (var b in bindings)
        {
            // Render one binding at a time so we control the terminator per line.
            var rendered = TriggerDirectiveParser.Render(new[] { b });
            // Render uses Environment.NewLine (or sb.AppendLine which is Environment.NewLine).
            // Strip whatever terminator it appended and replace with the detected newline.
            var content = rendered.TrimEnd('\r', '\n');
            directives.Add(new Line(content, newline, -1));
        }

        // Find the insertion index: immediately after the last surviving comment line so the
        // directives sit at the end of the comment block (and before any blank-line separator
        // that pads down to the body). When no comment lines survive (header was empty or
        // contained only blanks), default to the very top so the directives form the entire
        // header.
        int insertAt = 0;
        for (int i = surviving.Count - 1; i >= 0; i--)
        {
            if (IsComment(surviving[i].Content))
            {
                insertAt = i + 1;
                break;
            }
        }

        // Rebuild header.
        var sb = new StringBuilder(source.Length + (directives.Count * 64));
        for (int i = 0; i < surviving.Count; i++)
        {
            if (i == insertAt)
            {
                AppendLines(sb, directives);
            }
            AppendLine(sb, surviving[i]);
        }
        if (insertAt >= surviving.Count)
        {
            AppendLines(sb, directives);

            // No surviving header at all + directives + non-empty body → ensure a blank-line
            // separator so the directives don't visually merge into the body. Each directive
            // line already carries a terminator, so a single extra newline gives one blank.
            if (directives.Count > 0
                && surviving.Count == 0
                && body.Length > 0
                && !StartsWithNewline(body))
            {
                sb.Append(newline);
            }
        }

        sb.Append(body);
        return sb.ToString();
    }

    // ─── internals ─────────────────────────────────────────────────────────────────────

    private readonly struct Line
    {
        public Line(string content, string terminator, int start)
        {
            Content = content;
            Terminator = terminator;
            Start = start;
        }

        public string Content { get; }
        public string Terminator { get; }
        public int Start { get; }
    }

    private static List<Line> SplitLines(string source)
    {
        var lines = new List<Line>();
        int i = 0;
        while (i < source.Length)
        {
            int start = i;
            while (i < source.Length && source[i] != '\r' && source[i] != '\n') i++;
            int contentEnd = i;

            string terminator = string.Empty;
            if (i < source.Length)
            {
                if (source[i] == '\r')
                {
                    if (i + 1 < source.Length && source[i + 1] == '\n')
                    {
                        terminator = "\r\n";
                        i += 2;
                    }
                    else
                    {
                        terminator = "\r";
                        i++;
                    }
                }
                else
                {
                    terminator = "\n";
                    i++;
                }
            }

            lines.Add(new Line(source.Substring(start, contentEnd - start), terminator, start));
        }
        return lines;
    }

    private static void AppendLine(StringBuilder sb, Line line)
    {
        sb.Append(line.Content);
        sb.Append(line.Terminator);
    }

    private static void AppendLines(StringBuilder sb, IEnumerable<Line> lines)
    {
        foreach (var l in lines) AppendLine(sb, l);
    }

    private static bool StartsWithNewline(string s)
        => s.Length > 0 && (s[0] == '\r' || s[0] == '\n');

    private static bool IsBlank(string content) => content.TrimStart().Length == 0;

    private static bool IsComment(string content)
    {
        var t = content.TrimStart();
        return t.StartsWith("//", StringComparison.Ordinal);
    }

    /// <summary>
    /// Mirrors the prefix detection in <see cref="TriggerDirectiveParser.Parse"/>: a line is
    /// a directive iff it's a comment whose first non-whitespace token after the leading
    /// <c>//</c> is the case-insensitive keyword <c>@trigger</c> (followed by whitespace or
    /// end-of-line). Embedded directives such as
    /// <c>// EXAMPLE: // @trigger Build.SolutionBuildDone</c> are NOT detected — the parser
    /// strips only one <c>//</c> prefix, so the embedded directive is invisible to it; we
    /// must agree with that or round-trip would lose user comments.
    /// </summary>
    private static bool IsTriggerDirective(string content)
    {
        var t = content.TrimStart();
        if (!t.StartsWith("//", StringComparison.Ordinal)) return false;

        var afterSlashes = t.Substring(2).TrimStart();
        const string keyword = "@trigger";
        if (!afterSlashes.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)) return false;

        // Guard against "@triggered" / "@triggers" — keyword must be followed by whitespace
        // or end-of-line.
        return afterSlashes.Length == keyword.Length
            || char.IsWhiteSpace(afterSlashes[keyword.Length]);
    }
}
