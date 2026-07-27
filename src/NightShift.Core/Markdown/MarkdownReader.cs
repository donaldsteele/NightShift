using System.Text;
using System.Text.RegularExpressions;
using NightShift.Core.Preflight;

namespace NightShift.Core.Markdown;

/// <summary>
/// Parses a plan file into a <see cref="MarkdownDocument"/>.
/// </summary>
/// <remarks>
/// <para>
/// A block-level walk over <see cref="MarkdownLines"/> — the same fence rule the dashboard tally
/// uses — with inline text handed to <see cref="MarkdownInlineReader"/>.
/// </para>
/// <para>
/// <b>Deliberate non-goals.</b> Each of these is left out for a reason, not for lack of time, and
/// adding one would break something this corpus depends on:
/// </para>
/// <list type="bullet">
/// <item><b>Raw HTML</b>, block or inline. Every <c>&lt;…&gt;</c> in this repo's plan file sits
/// inside a backtick span — <c>`&lt;Nullable&gt;enable&lt;/Nullable&gt;`</c>,
/// <c>`--token-limit &lt;n|max&gt;`</c>. Interpreting tags would eat that content, and would add a
/// markup-injection surface for no benefit.</item>
/// <item><b>Setext headings</b> (<c>===</c> / <c>---</c> underlines). CommonMark makes a
/// <c>---</c> after a paragraph an H2. These plans use standalone <c>---</c> as section rules —
/// a dozen of them per file — and zero <c>===</c>. Rules win.</item>
/// <item><b>Indented (four-space) code blocks.</b> They collide with the two-to-four space
/// indentation these plans use for nested list items.</item>
/// <item><b>Reference-style links and link definitions</b>, whose <c>[label]:</c> syntax is one
/// bracket away from <c>- [x]</c>.</item>
/// <item><b>Footnotes, entity escapes, autolinks, lazy continuation, and block quotes nested more
/// than one level.</b></item>
/// </list>
/// <para>
/// Anything unrecognised falls through as literal text. For a document whose bytes the user is
/// about to edit, showing something odd is always better than showing nothing.
/// </para>
/// </remarks>
public static partial class MarkdownReader
{
    /// <summary>Parses <paramref name="text"/>. Null, empty or whitespace-only gives an empty document.</summary>
    public static MarkdownDocument Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return MarkdownDocument.Empty;
        }

        var lines = MarkdownLines.Walk(text).ToList();
        return new MarkdownDocument(ReadBlocks(lines, 0, lines.Count));
    }

    /// <summary>Parses the half-open line range <c>[start, end)</c>.</summary>
    static List<MarkdownBlock> ReadBlocks(List<MarkdownLine> lines, int start, int end)
    {
        var blocks = new List<MarkdownBlock>();
        var index = start;

        while (index < end)
        {
            var line = lines[index];

            if (line.IsDelimiter)
            {
                blocks.Add(ReadCode(lines, ref index, end));
                continue;
            }

            if (line.InFence)
            {
                // Only reachable when a range begins inside an unclosed fence; treat it as code so
                // nothing is silently dropped.
                blocks.Add(ReadCode(lines, ref index, end));
                continue;
            }

            if (line.Text.Trim().Length == 0)
            {
                index++;
                continue;
            }

            if (RulePattern().IsMatch(line.Text))
            {
                blocks.Add(new RuleBlock());
                index++;
                continue;
            }

            var heading = HeadingPattern().Match(line.Text);
            if (heading.Success)
            {
                blocks.Add(ReadHeading(line.Text, heading));
                index++;
                continue;
            }

            if (QuotePattern().IsMatch(line.Text))
            {
                blocks.Add(ReadQuote(lines, ref index, end));
                continue;
            }

            if (TryReadTable(lines, ref index, end, out var table))
            {
                blocks.Add(table);
                continue;
            }

            if (BulletPattern().IsMatch(line.Text) || OrderedPattern().IsMatch(line.Text))
            {
                blocks.Add(ReadList(lines, ref index, end));
                continue;
            }

            blocks.Add(ReadParagraph(lines, ref index, end));
        }

        return blocks;
    }

    // ── Blocks ─────────────────────────────────────────────────────────────────────────────────

    static HeadingBlock ReadHeading(string text, Match heading)
    {
        var level = heading.Groups["hashes"].Length;
        var content = text[heading.Groups["hashes"].Length..].Trim();

        int? milestone = null;
        var marker = MilestoneMarker.None;

        var m = PlanParser.MilestoneHeadingPattern().Match(text);
        if (m.Success)
        {
            milestone = int.Parse(m.Groups["n"].ValueSpan);

            // Blocked is tested first and wins, exactly as the tally does it.
            marker = PlanParser.MarksBlocked(text) ? MilestoneMarker.Blocked
                : PlanParser.MarksDelivered(text) ? MilestoneMarker.Delivered
                : MilestoneMarker.None;
        }

        return new HeadingBlock(level, MarkdownInlineReader.Read(content), milestone, marker);
    }

    static CodeBlock ReadCode(List<MarkdownLine> lines, ref int index, int end)
    {
        var language = lines[index].FenceInfo;
        var body = new List<string>();

        if (lines[index].IsDelimiter)
        {
            index++;
        }

        while (index < end && lines[index].InFence && !lines[index].IsDelimiter)
        {
            body.Add(lines[index].Text);
            index++;
        }

        // The closing delimiter, when there is one.
        if (index < end && lines[index].IsDelimiter)
        {
            index++;
        }

        return new CodeBlock(
            string.IsNullOrEmpty(language) ? null : language,
            string.Join('\n', body));
    }

    static QuoteBlock ReadQuote(List<MarkdownLine> lines, ref int index, int end)
    {
        var inner = new List<MarkdownLine>();

        while (index < end && !lines[index].InFence && QuotePattern().IsMatch(lines[index].Text))
        {
            inner.Add(lines[index] with { Text = StripQuoteMarker(lines[index].Text) });
            index++;
        }

        return new QuoteBlock(ReadBlocks(inner, 0, inner.Count));
    }

    /// <summary>Removes one <c>&gt;</c> and the single optional space after it.</summary>
    static string StripQuoteMarker(string text)
    {
        var trimmed = text.TrimStart();
        var body = trimmed[1..];
        return body.StartsWith(' ') ? body[1..] : body;
    }

    static ParagraphBlock ReadParagraph(List<MarkdownLine> lines, ref int index, int end)
    {
        var parts = new List<string>();

        while (index < end && IsParagraphContinuation(lines, index, parts.Count == 0))
        {
            parts.Add(lines[index].Text.Trim());
            index++;
        }

        // Hard-wrapped source joins with single spaces: these plans wrap prose at about 95
        // columns, and rendering each source line as its own paragraph would be unreadable.
        return new ParagraphBlock(MarkdownInlineReader.Read(string.Join(' ', parts)));
    }

    static bool IsParagraphContinuation(List<MarkdownLine> lines, int index, bool isFirst)
    {
        var line = lines[index];

        if (line.InFence || line.Text.Trim().Length == 0)
        {
            return false;
        }

        if (isFirst)
        {
            return true;
        }

        return !RulePattern().IsMatch(line.Text)
            && !HeadingPattern().IsMatch(line.Text)
            && !QuotePattern().IsMatch(line.Text)
            && !BulletPattern().IsMatch(line.Text)
            && !OrderedPattern().IsMatch(line.Text);
    }

    // ── Lists ──────────────────────────────────────────────────────────────────────────────────

    static ListBlock ReadList(List<MarkdownLine> lines, ref int index, int end)
    {
        var ordered = OrderedPattern().IsMatch(lines[index].Text);
        var indent = IndentOf(lines[index].Text);
        var items = new List<ListItem>();

        while (index < end)
        {
            var line = lines[index];

            if (line.InFence)
            {
                break;
            }

            if (line.Text.Trim().Length == 0)
            {
                // A blank line ends the list unless another item of the same list follows.
                var lookahead = index + 1;
                while (lookahead < end && lines[lookahead].Text.Trim().Length == 0)
                {
                    lookahead++;
                }

                if (lookahead >= end || !StartsItem(lines[lookahead].Text, ordered, indent))
                {
                    break;
                }

                index = lookahead;
                continue;
            }

            if (!StartsItem(line.Text, ordered, indent))
            {
                break;
            }

            items.Add(ReadListItem(lines, ref index, end, indent));
        }

        return new ListBlock(ordered, items);
    }

    static bool StartsItem(string text, bool ordered, int indent)
    {
        if (IndentOf(text) != indent)
        {
            return false;
        }

        return ordered ? OrderedPattern().IsMatch(text) : BulletPattern().IsMatch(text);
    }

    static ListItem ReadListItem(List<MarkdownLine> lines, ref int index, int end, int indent)
    {
        var first = lines[index].Text;
        var marker = (OrderedPattern().Match(first) is { Success: true } o
            ? o
            : BulletPattern().Match(first)).Value;

        var rest = first[marker.Length..];

        TaskMark? task = null;
        var checkbox = PlanParser.CheckboxPattern().Match(first);
        if (checkbox.Success)
        {
            task = checkbox.Groups["mark"].ValueSpan[0] switch
            {
                'x' or 'X' => TaskMark.Done,
                '!' => TaskMark.Blocked,
                _ => TaskMark.Todo,
            };

            // Drop the "[x] " so the mark is not also printed as text.
            var bracket = rest.IndexOf(']', StringComparison.Ordinal);
            rest = bracket >= 0 ? rest[(bracket + 1)..].TrimStart() : rest;
        }

        var body = new List<MarkdownLine> { lines[index] with { Text = rest } };
        index++;

        // Continuation and nesting: anything indented past the marker belongs to this item, and so
        // does a lazily-wrapped plain line.
        while (index < end)
        {
            var line = lines[index];

            if (line.InFence)
            {
                body.Add(line);
                index++;
                continue;
            }

            if (line.Text.Trim().Length == 0)
            {
                var lookahead = index + 1;
                if (lookahead >= end || IndentOf(lines[lookahead].Text) <= indent)
                {
                    break;
                }

                body.Add(line);
                index++;
                continue;
            }

            var lineIndent = IndentOf(line.Text);

            if (lineIndent > indent)
            {
                body.Add(line with { Text = Dedent(line.Text, indent + marker.Length) });
                index++;
                continue;
            }

            break;
        }

        return new ListItem(task, ReadBlocks(body, 0, body.Count));
    }

    static int IndentOf(string text)
    {
        var count = 0;
        foreach (var c in text)
        {
            if (c == ' ')
            {
                count++;
            }
            else if (c == '\t')
            {
                count += 4;
            }
            else
            {
                break;
            }
        }

        return count;
    }

    /// <summary>Removes up to <paramref name="amount"/> leading spaces, never more than there are.</summary>
    static string Dedent(string text, int amount)
    {
        var removed = 0;
        var index = 0;

        while (index < text.Length && removed < amount && text[index] is ' ' or '\t')
        {
            removed += text[index] == '\t' ? 4 : 1;
            index++;
        }

        return text[index..];
    }

    // ── Tables ─────────────────────────────────────────────────────────────────────────────────

    static bool TryReadTable(List<MarkdownLine> lines, ref int index, int end, out TableBlock table)
    {
        table = null!;

        if (index + 1 >= end
            || !lines[index].Text.Contains('|', StringComparison.Ordinal)
            || lines[index + 1].InFence
            || !TableDividerPattern().IsMatch(lines[index + 1].Text))
        {
            return false;
        }

        var header = SplitRow(lines[index].Text);
        var alignments = SplitRow(lines[index + 1].Text).Select(AlignmentOf).ToList();
        index += 2;

        var rows = new List<IReadOnlyList<TableCell>>();
        while (index < end
            && !lines[index].InFence
            && lines[index].Text.Contains('|', StringComparison.Ordinal)
            && lines[index].Text.Trim().Length > 0)
        {
            var cells = SplitRow(lines[index].Text)
                .Select(cell => new TableCell(MarkdownInlineReader.Read(cell)))
                .ToList();

            // Short rows are padded rather than rejected: a half-typed row in a file the user is
            // editing must still render.
            while (cells.Count < alignments.Count)
            {
                cells.Add(new TableCell([]));
            }

            rows.Add(cells);
            index++;
        }

        table = new TableBlock(
            alignments,
            header.Select(cell => new TableCell(MarkdownInlineReader.Read(cell))).ToList(),
            rows);

        return true;
    }

    /// <summary>Splits a pipe row, honouring <c>\|</c> and dropping the optional outer pipes.</summary>
    static List<string> SplitRow(string text)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var body = text.Trim();

        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == '\\' && i + 1 < body.Length && body[i + 1] == '|')
            {
                cell.Append('|');
                i++;
                continue;
            }

            if (body[i] == '|')
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
                continue;
            }

            cell.Append(body[i]);
        }

        cells.Add(cell.ToString().Trim());

        if (cells.Count > 0 && cells[0].Length == 0)
        {
            cells.RemoveAt(0);
        }

        if (cells.Count > 0 && cells[^1].Length == 0)
        {
            cells.RemoveAt(cells.Count - 1);
        }

        return cells;
    }

    static ColumnAlignment AlignmentOf(string spec)
    {
        var trimmed = spec.Trim();
        var left = trimmed.StartsWith(':');
        var right = trimmed.EndsWith(':');

        return (left, right) switch
        {
            (true, true) => ColumnAlignment.Center,
            (true, false) => ColumnAlignment.Left,
            (false, true) => ColumnAlignment.Right,
            _ => ColumnAlignment.Default,
        };
    }

    // ── Patterns ───────────────────────────────────────────────────────────────────────────────

    /// <summary>An ATX heading. Requires a space and content, matching the tally's rule.</summary>
    [GeneratedRegex(@"^(?<hashes>\#{1,6})[ \t]+\S", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingPattern();

    /// <summary>A thematic break: three or more of the same mark, alone on the line.</summary>
    [GeneratedRegex(@"^[ \t]{0,3}((-[ \t]*){3,}|(_[ \t]*){3,}|(\*[ \t]*){3,})$", RegexOptions.CultureInvariant)]
    private static partial Regex RulePattern();

    [GeneratedRegex(@"^[ \t]*>", RegexOptions.CultureInvariant)]
    private static partial Regex QuotePattern();

    [GeneratedRegex(@"^[ \t]*[-*+][ \t]+", RegexOptions.CultureInvariant)]
    private static partial Regex BulletPattern();

    [GeneratedRegex(@"^[ \t]*\d+[.)][ \t]+", RegexOptions.CultureInvariant)]
    private static partial Regex OrderedPattern();

    /// <summary>The <c>|:---|---:|</c> row that turns the line above it into a table header.</summary>
    [GeneratedRegex(@"^[ \t]*\|?[ \t]*:?-{1,}:?[ \t]*(\|[ \t]*:?-{1,}:?[ \t]*)*\|?[ \t]*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex TableDividerPattern();
}
