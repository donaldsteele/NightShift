using System.Text;

namespace NightShift.Core.Markdown;

/// <summary>
/// Turns one block's text into <see cref="MarkdownInline"/> runs.
/// </summary>
/// <remarks>
/// <para>
/// A single left-to-right scan with explicit lookahead, not a regex. Two reasons: one real table
/// cell in these plans runs to about 1200 characters and a backtracking pattern over nested
/// delimiters is where that becomes visible; and precedence has to be exact — <b>code spans bind
/// tightest</b>, so <c>`**x**`</c> stays literal. This repo's own plan has 411 code spans, many of
/// them containing asterisks, underscores and angle brackets.
/// </para>
/// <para>
/// <b>It never throws and never drops a character.</b> An unmatched <c>**</c> is emitted as two
/// literal asterisks. That is the right failure mode for a document the user is about to edit: a
/// reader that swallowed text would make the rendered view disagree with the bytes on disk.
/// </para>
/// </remarks>
internal static class MarkdownInlineReader
{
    /// <summary>Characters that can begin something other than literal text.</summary>
    static readonly char[] Specials = ['\\', '`', '*', '_', '~', '['];

    /// <summary>Characters a backslash may escape. Anything else keeps its backslash.</summary>
    const string Escapable = @"\`*_~[]()#+-.!|<>";

    public static IReadOnlyList<MarkdownInline> Read(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var inlines = new List<MarkdownInline>();
        var literal = new StringBuilder();
        var index = 0;

        while (index < text.Length)
        {
            var next = text.IndexOfAny(Specials, index);
            if (next < 0)
            {
                literal.Append(text, index, text.Length - index);
                break;
            }

            literal.Append(text, index, next - index);
            index = next;

            // Escapes are resolved into the literal buffer rather than emitted as their own run,
            // so `a\*b` stays one TextRun instead of three.
            if (text[index] == '\\'
                && index + 1 < text.Length
                && Escapable.Contains(text[index + 1], StringComparison.Ordinal))
            {
                literal.Append(text[index + 1]);
                index += 2;
                continue;
            }

            var consumed = TryReadSpecial(text, ref index, out var inline);
            if (!consumed)
            {
                // Not the start of anything: it is just this character, literally.
                literal.Append(text[index]);
                index++;
                continue;
            }

            Flush(inlines, literal);
            if (inline is not null)
            {
                inlines.Add(inline);
            }
        }

        Flush(inlines, literal);
        return inlines;
    }

    /// <summary>
    /// Tries to read one construct starting at <paramref name="index"/>. Returns false and leaves
    /// <paramref name="index"/> alone when the character does not in fact start one.
    /// </summary>
    static bool TryReadSpecial(string text, ref int index, out MarkdownInline? inline)
    {
        inline = null;

        switch (text[index])
        {
            case '`':
                return TryReadCode(text, ref index, out inline);
            case '[':
                return TryReadLink(text, ref index, out inline);
            case '~':
                return TryReadEmphasis(text, ref index, '~', out inline);
            case '*':
            case '_':
                return TryReadEmphasis(text, ref index, text[index], out inline);
            default:
                return false;
        }
    }

    /// <summary>
    /// A code span: a run of N backticks closed by the next run of exactly N. Nothing inside is
    /// interpreted.
    /// </summary>
    static bool TryReadCode(string text, ref int index, out MarkdownInline? inline)
    {
        inline = null;

        var open = RunLength(text, index, '`');
        var search = index + open;

        while (search < text.Length)
        {
            var tick = text.IndexOf('`', search);
            if (tick < 0)
            {
                return false;
            }

            var close = RunLength(text, tick, '`');
            if (close == open)
            {
                inline = new CodeRun(text[(index + open)..tick]);
                index = tick + close;
                return true;
            }

            search = tick + close;
        }

        return false;
    }

    /// <summary><c>[text](url)</c>. Reference-style links are a stated non-goal.</summary>
    static bool TryReadLink(string text, ref int index, out MarkdownInline? inline)
    {
        inline = null;

        var close = text.IndexOf(']', index + 1);
        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(')
        {
            return false;
        }

        var end = text.IndexOf(')', close + 2);
        if (end < 0)
        {
            return false;
        }

        inline = new LinkRun(text[(index + 1)..close], text[(close + 2)..end].Trim());
        index = end + 1;
        return true;
    }

    /// <summary>
    /// <c>***x***</c>, <c>**x**</c>, <c>*x*</c>, <c>__x__</c>, <c>_x_</c>, <c>~~x~~</c>.
    /// </summary>
    /// <remarks>
    /// Flanking is checked both ends, which is what keeps <c>a*b*c</c> literal (a stated non-goal)
    /// and — the case that actually matters here — stops two access-key markers in one sentence
    /// from pairing up: <c>"the next part of the _window"</c> followed later by <c>"_style"</c>
    /// must not italicise everything between them. The closer would be preceded by a space, so it
    /// is rejected.
    /// </remarks>
    static bool TryReadEmphasis(string text, ref int index, char marker, out MarkdownInline? inline)
    {
        inline = null;

        var run = RunLength(text, index, marker);

        // `~` is only ever strikethrough, and only doubled.
        if (marker == '~' && run != 2)
        {
            return false;
        }

        if (run > 3)
        {
            return false;
        }

        var kind = marker == '~'
            ? StyleKind.Strikethrough
            : run switch
            {
                3 => StyleKind.BoldItalic,
                2 => StyleKind.Bold,
                _ => StyleKind.Italic,
            };

        var contentStart = index + run;
        if (contentStart >= text.Length || char.IsWhiteSpace(text[contentStart]))
        {
            return false;
        }

        if (!IsLeftFlanking(text, index))
        {
            return false;
        }

        var search = contentStart;
        while (search < text.Length)
        {
            var candidate = text.IndexOf(marker, search);
            if (candidate < 0)
            {
                return false;
            }

            // A backslash-escaped marker is not a delimiter.
            if (candidate > 0 && text[candidate - 1] == '\\')
            {
                search = candidate + 1;
                continue;
            }

            var closeRun = RunLength(text, candidate, marker);
            if (closeRun >= run && candidate > contentStart && !char.IsWhiteSpace(text[candidate - 1]))
            {
                inline = new StyledRun(kind, Read(text[contentStart..candidate]));
                index = candidate + run;
                return true;
            }

            search = candidate + closeRun;
        }

        return false;
    }

    /// <summary>
    /// True when the delimiter at <paramref name="index"/> can open. <c>*</c> and <c>~</c> may open
    /// anywhere; <c>_</c> may not open inside a word, so <c>snake_case</c> survives.
    /// </summary>
    static bool IsLeftFlanking(string text, int index)
    {
        if (text[index] != '_')
        {
            return true;
        }

        return index == 0 || !char.IsLetterOrDigit(text[index - 1]);
    }

    static int RunLength(string text, int index, char c)
    {
        var length = 0;
        while (index + length < text.Length && text[index + length] == c)
        {
            length++;
        }

        return length;
    }

    static void Flush(List<MarkdownInline> inlines, StringBuilder literal)
    {
        if (literal.Length == 0)
        {
            return;
        }

        // Merge with a preceding literal so an escape does not fragment a sentence into runs.
        if (inlines.Count > 0 && inlines[^1] is TextRun previous)
        {
            inlines[^1] = new TextRun(previous.Text + literal.ToString());
        }
        else
        {
            inlines.Add(new TextRun(literal.ToString()));
        }

        literal.Clear();
    }
}
