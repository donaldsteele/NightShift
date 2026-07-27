namespace NightShift.Core.Markdown;

/// <summary>One source line, and what the fence state was when it was read.</summary>
/// <param name="Text">The line with its trailing CR removed. Never null.</param>
/// <param name="InFence">
/// True for everything a fenced code block owns — its opening delimiter, its contents, and its
/// closing delimiter. The tally treats all three the same way: not content.
/// </param>
/// <param name="IsDelimiter">
/// True for the ``` or ~~~ lines themselves. <see cref="InFence"/> is also true for these, so a
/// consumer that only wants prose can ignore this; a consumer building a code block needs it to
/// know which lines are the fence rather than the code.
/// </param>
/// <param name="FenceInfo">
/// The info string from the opening delimiter — <c>csharp</c> for <c>```csharp</c> — carried on
/// every line of that fence. Null outside a fence, and empty when the delimiter carried nothing.
/// </param>
internal readonly record struct MarkdownLine(
    string Text,
    bool InFence,
    bool IsDelimiter,
    string? FenceInfo);

/// <summary>
/// The repo's single definition of "which lines are inside a fenced code block", shared by
/// <see cref="Preflight.PlanParser"/> and <see cref="MarkdownReader"/>.
/// </summary>
/// <remarks>
/// <para>
/// It exists because a plan that documents its own conventions — as this repo's does — shows
/// example checkboxes and example milestone headings inside a fence, and counting those would make
/// the dashboard's "12 of 30" quietly wrong. The viewer has to agree with the tally about that or
/// the two disagree on screen, so there is one walk rather than two.
/// </para>
/// <para>
/// <b>The rule is deliberately naive and must stay that way.</b> Any line whose left-trimmed text
/// starts with three backticks or three tildes toggles the state — the fence character is not
/// matched, so a block opened with backticks and closed with tildes still toggles, fence length is
/// ignored, and an unclosed fence swallows the rest of the file. CommonMark says otherwise on all
/// three counts. Tightening any of them changes what the dashboard counts, so it is a change to the
/// product, not a bug fix.
/// </para>
/// </remarks>
internal static class MarkdownLines
{
    const string BacktickFence = "```";
    const string TildeFence = "~~~";

    /// <summary>Walks <paramref name="text"/> line by line, tracking fence state.</summary>
    /// <remarks>
    /// Splits on <c>'\n'</c> alone and trims a trailing <c>'\r'</c>, so CRLF and LF files walk
    /// identically. Lazy, so a caller that stops early does not scan the rest of the file.
    /// </remarks>
    public static IEnumerable<MarkdownLine> Walk(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        var inFence = false;
        string? fenceInfo = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith(BacktickFence, StringComparison.Ordinal) ||
                trimmed.StartsWith(TildeFence, StringComparison.Ordinal))
            {
                if (inFence)
                {
                    // The closing delimiter still belongs to the fence it closes, so it reports the
                    // info string of the block it is ending rather than one read off itself.
                    yield return new MarkdownLine(line, InFence: true, IsDelimiter: true, fenceInfo);
                    inFence = false;
                    fenceInfo = null;
                }
                else
                {
                    fenceInfo = ReadInfo(trimmed);
                    inFence = true;
                    yield return new MarkdownLine(line, InFence: true, IsDelimiter: true, fenceInfo);
                }

                continue;
            }

            yield return new MarkdownLine(line, inFence, IsDelimiter: false, inFence ? fenceInfo : null);
        }
    }

    /// <summary>
    /// Everything after the delimiter's run of fence characters, trimmed — the language tag.
    /// </summary>
    static string ReadInfo(string trimmedDelimiter)
    {
        var fenceChar = trimmedDelimiter[0];

        var index = 0;
        while (index < trimmedDelimiter.Length && trimmedDelimiter[index] == fenceChar)
        {
            index++;
        }

        return trimmedDelimiter[index..].Trim();
    }
}
