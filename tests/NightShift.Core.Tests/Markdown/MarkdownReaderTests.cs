using NightShift.Core.Markdown;

namespace NightShift.Core.Tests.Markdown;

public sealed class MarkdownReaderTests
{
    static IReadOnlyList<MarkdownBlock> Blocks(string text) => MarkdownReader.Read(text).Blocks;

    static T Single<T>(string text) where T : MarkdownBlock => Assert.IsType<T>(Assert.Single(Blocks(text)));

    /// <summary>The visible text of a run of inlines, with markup removed.</summary>
    static string Flatten(IEnumerable<MarkdownInline> inlines) =>
        string.Concat(inlines.Select(inline => inline switch
        {
            TextRun text => text.Text,
            CodeRun code => code.Text,
            LinkRun link => link.Text,
            StyledRun styled => Flatten(styled.Inlines),
            _ => string.Empty,
        }));

    [Theory]
    [InlineData("# One", 1)]
    [InlineData("## Two", 2)]
    [InlineData("###### Six", 6)]
    public void Heading_levels_are_the_hash_count(string text, int level) =>
        Assert.Equal(level, Single<HeadingBlock>(text).Level);

    [Fact]
    public void A_hash_with_no_space_is_not_a_heading()
    {
        // The tally's HeadingPattern requires whitespace then content; the two must agree, or a
        // "#nothashtag" line would be a heading here and a paragraph there.
        var block = Single<ParagraphBlock>("#NotAHeading");

        Assert.Equal("#NotAHeading", Flatten(block.Inlines));
    }

    [Fact]
    public void Hard_wrapped_lines_join_into_one_paragraph_with_single_spaces()
    {
        var block = Single<ParagraphBlock>("The quick brown\nfox jumps over\nthe lazy dog.");

        Assert.Equal("The quick brown fox jumps over the lazy dog.", Flatten(block.Inlines));
    }

    [Fact]
    public void A_blank_line_separates_paragraphs()
    {
        var blocks = Blocks("First.\n\nSecond.");

        Assert.Equal(2, blocks.Count);
        Assert.All(blocks, block => Assert.IsType<ParagraphBlock>(block));
    }

    // ── Task marks ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("- [ ] open", TaskMark.Todo)]
    [InlineData("- [x] done", TaskMark.Done)]
    [InlineData("- [X] done", TaskMark.Done)]
    [InlineData("- [!] blocked", TaskMark.Blocked)]
    [InlineData("* [!] blocked", TaskMark.Blocked)]
    [InlineData("+ [x] done", TaskMark.Done)]
    public void The_three_task_marks_are_recognised_on_every_bullet_character(string text, TaskMark expected)
    {
        var list = Single<ListBlock>(text);

        Assert.Equal(expected, Assert.Single(list.Items).Task);
    }

    [Fact]
    public void A_task_item_does_not_also_print_its_own_mark()
    {
        var item = Assert.Single(Single<ListBlock>("- [!] needs a person").Items);
        var paragraph = Assert.IsType<ParagraphBlock>(Assert.Single(item.Blocks));

        Assert.Equal("needs a person", Flatten(paragraph.Inlines));
    }

    [Fact]
    public void An_ordinary_bullet_has_no_task_mark() =>
        Assert.Null(Assert.Single(Single<ListBlock>("- just a bullet").Items).Task);

    [Fact]
    public void Ordered_lists_are_marked_ordered()
    {
        Assert.True(Single<ListBlock>("1. first\n2. second").Ordered);
        Assert.False(Single<ListBlock>("- first\n- second").Ordered);
    }

    [Fact]
    public void A_nested_item_becomes_a_list_inside_its_parent_item()
    {
        // Two-space indentation, which these plans use everywhere. It must be a nested list and
        // never an indented code block -- that is why four-space code blocks are a non-goal.
        var outer = Single<ListBlock>("- parent\n  - child\n- sibling");

        Assert.Equal(2, outer.Items.Count);
        Assert.Contains(outer.Items[0].Blocks, block => block is ListBlock);
    }

    // ── Milestone headings ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_milestone_heading_carries_its_number()
    {
        var heading = Single<HeadingBlock>("### M13 — Roster-driven widgets (M)");

        Assert.Equal(13, heading.MilestoneNumber);
        Assert.Equal(MilestoneMarker.None, heading.Marker);
    }

    [Fact]
    public void An_ordinary_heading_has_no_milestone_number() =>
        Assert.Null(Single<HeadingBlock>("### Sizing & sequencing notes").MilestoneNumber);

    [Fact]
    public void A_delivered_milestone_heading_reports_delivered() =>
        Assert.Equal(
            MilestoneMarker.Delivered,
            Single<HeadingBlock>("### M10 — Packaging & release (S/M) — **delivered 2026-07-26**").Marker);

    [Fact]
    public void Blocked_beats_delivered_on_a_milestone_heading()
    {
        // The tally checks blocked first and lets it win; a heading that says both must not be
        // quietly reported as shipped.
        var heading = Single<HeadingBlock>("### M9 — Thing — **delivered 2026-01-01** **Blocked:** waiting");

        Assert.Equal(MilestoneMarker.Blocked, heading.Marker);
    }

    // ── Code ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_fenced_block_keeps_its_language_and_its_contents_verbatim()
    {
        var code = Single<CodeBlock>("```csharp\nvar x = 1;\n\n# not a heading\n```");

        Assert.Equal("csharp", code.Language);
        Assert.Equal("var x = 1;\n\n# not a heading", code.Text);
    }

    [Fact]
    public void A_fence_with_no_info_string_has_no_language() =>
        Assert.Null(Single<CodeBlock>("```\nplain\n```").Language);

    [Fact]
    public void A_checkbox_inside_a_fence_is_code_and_not_a_task_item()
    {
        // The same invariant the dashboard tally holds. It is the reason the fence walk is shared
        // rather than written twice.
        var code = Single<CodeBlock>("```\n- [ ] an example, not real work\n```");

        Assert.Equal("- [ ] an example, not real work", code.Text);
    }

    [Fact]
    public void An_unclosed_fence_swallows_the_rest_of_the_file()
    {
        // Matches the tally's behaviour exactly. Documented, not accidental.
        var code = Single<CodeBlock>("```\nstill code\nalso code");

        Assert.Equal("still code\nalso code", code.Text);
    }

    // ── Quotes ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_quote_containing_bold_and_a_nested_list_keeps_both()
    {
        // The dominant construct in this repo's own plan: 251 quote lines, mostly "Refinement"
        // amendments that carry bold text and bullet lists.
        var quote = Single<QuoteBlock>("> **Refinement (2026-07-27).**\n>\n> - first\n> - second");

        Assert.Equal(2, quote.Blocks.Count);
        var paragraph = Assert.IsType<ParagraphBlock>(quote.Blocks[0]);
        Assert.Equal(StyleKind.Bold, Assert.IsType<StyledRun>(Assert.Single(paragraph.Inlines)).Kind);
        Assert.Equal(2, Assert.IsType<ListBlock>(quote.Blocks[1]).Items.Count);
    }

    [Fact]
    public void A_quote_ends_at_the_first_unquoted_line()
    {
        var blocks = Blocks("> quoted\nnot quoted");

        Assert.Equal(2, blocks.Count);
        Assert.IsType<QuoteBlock>(blocks[0]);
        Assert.IsType<ParagraphBlock>(blocks[1]);
    }

    // ── Tables ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_pipe_table_reads_its_header_alignments_and_rows()
    {
        var table = Single<TableBlock>(
            """
            | Concern | Choice | Why |
            |:---|:---:|---:|
            | UI | Avalonia | Linux |
            | PDF | Skia | parity |
            """);

        Assert.Equal(3, table.Header.Count);
        Assert.Equal(
            [ColumnAlignment.Left, ColumnAlignment.Center, ColumnAlignment.Right],
            table.Alignments);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("Skia", Flatten(table.Rows[1][1].Inlines));
    }

    [Fact]
    public void An_escaped_pipe_stays_inside_its_cell()
    {
        var table = Single<TableBlock>("| a | b |\n|---|---|\n| x \\| y | z |");

        Assert.Equal("x | y", Flatten(table.Rows[0][0].Inlines));
    }

    [Fact]
    public void A_short_row_is_padded_rather_than_rejected()
    {
        var table = Single<TableBlock>("| a | b | c |\n|---|---|---|\n| only one |");

        Assert.Equal(3, table.Rows[0].Count);
    }

    [Fact]
    public void A_pipe_line_with_no_divider_row_is_just_a_paragraph() =>
        Assert.IsType<ParagraphBlock>(Assert.Single(Blocks("this | that | other")));

    // ── Rules ──────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("---")]
    [InlineData("***")]
    [InlineData("___")]
    [InlineData("- - -")]
    public void Thematic_breaks_are_rules(string text) => Single<RuleBlock>(text);

    [Fact]
    public void A_rule_after_a_paragraph_is_still_a_rule()
    {
        // CommonMark would make this a setext H2. These plans use standalone --- as a section
        // rule a dozen times per file and never use setext, so rules win. Deliberate deviation.
        var blocks = Blocks("Some prose.\n\n---\n");

        Assert.IsType<ParagraphBlock>(blocks[0]);
        Assert.IsType<RuleBlock>(blocks[1]);
    }

    // ── Inlines ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("**bold**", StyleKind.Bold)]
    [InlineData("__bold__", StyleKind.Bold)]
    [InlineData("*italic*", StyleKind.Italic)]
    [InlineData("_italic_", StyleKind.Italic)]
    [InlineData("***both***", StyleKind.BoldItalic)]
    [InlineData("~~gone~~", StyleKind.Strikethrough)]
    public void Emphasis_kinds(string text, StyleKind expected)
    {
        var paragraph = Single<ParagraphBlock>(text);

        Assert.Equal(expected, Assert.IsType<StyledRun>(Assert.Single(paragraph.Inlines)).Kind);
    }

    [Fact]
    public void A_code_span_is_literal_and_is_never_parsed_again()
    {
        // The single most important inline rule for this corpus. Every angle bracket in this
        // repo's plan file lives inside a code span, and a reader that interpreted them would eat
        // the content.
        var paragraph = Single<ParagraphBlock>("Set `<Nullable>enable</Nullable>` in props.");
        var code = Assert.IsType<CodeRun>(paragraph.Inlines[1]);

        Assert.Equal("<Nullable>enable</Nullable>", code.Text);
    }

    [Fact]
    public void Emphasis_inside_a_code_span_stays_literal()
    {
        var paragraph = Single<ParagraphBlock>("`**not bold**`");

        Assert.Equal("**not bold**", Assert.IsType<CodeRun>(Assert.Single(paragraph.Inlines)).Text);
    }

    [Fact]
    public void An_unmatched_delimiter_is_emitted_as_literal_text()
    {
        var paragraph = Single<ParagraphBlock>("2 ** 3 is not emphasis");

        Assert.Equal("2 ** 3 is not emphasis", Flatten(paragraph.Inlines));
        Assert.DoesNotContain(paragraph.Inlines, inline => inline is StyledRun);
    }

    [Fact]
    public void Two_access_key_markers_in_one_sentence_do_not_pair_up()
    {
        // Real text from a plan: "_window" and "_style" are access-key notation, not emphasis.
        // The closing delimiter is preceded by a space, so it cannot close.
        var paragraph = Single<ParagraphBlock>("the next part of the _window and the paragraph _style");

        Assert.DoesNotContain(paragraph.Inlines, inline => inline is StyledRun);
    }

    [Fact]
    public void Underscores_inside_a_word_are_not_emphasis()
    {
        var paragraph = Single<ParagraphBlock>("snake_case_name stays whole");

        Assert.Equal("snake_case_name stays whole", Flatten(paragraph.Inlines));
    }

    [Fact]
    public void A_backslash_escape_yields_the_literal_character_in_one_run()
    {
        var paragraph = Single<ParagraphBlock>(@"a\*b\*c");

        Assert.Equal("a*b*c", Flatten(paragraph.Inlines));
        Assert.Single(paragraph.Inlines);
    }

    [Fact]
    public void An_inline_link_keeps_its_text_and_destination()
    {
        var paragraph = Single<ParagraphBlock>("see [the docs](https://example.com/x) for more");
        var link = Assert.IsType<LinkRun>(paragraph.Inlines[1]);

        Assert.Equal("the docs", link.Text);
        Assert.Equal("https://example.com/x", link.Url);
    }

    [Fact]
    public void Emphasis_nests()
    {
        var paragraph = Single<ParagraphBlock>("**bold with `code` inside**");
        var bold = Assert.IsType<StyledRun>(Assert.Single(paragraph.Inlines));

        Assert.Contains(bold.Inlines, inline => inline is CodeRun);
    }

    // ── Degenerate input ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t\n  ")]
    public void Empty_input_gives_an_empty_document(string? text) =>
        Assert.Empty(MarkdownReader.Read(text).Blocks);

    [Fact]
    public void Crlf_reads_identically_to_lf()
    {
        const string Source = "# Title\n\n- [x] done\n\n> quoted\n";

        Assert.Equal(
            MarkdownReader.Read(Source).Blocks.Count,
            MarkdownReader.Read(Source.Replace("\n", "\r\n")).Blocks.Count);
    }

    [Fact]
    public void No_input_makes_the_reader_throw()
    {
        // A plan file is user-controlled text and the window must always render something. This
        // walks the pathological shapes in one pass rather than asserting a shape for each.
        string[] hostile =
        [
            "```", "~~~", "|", "|---|", "- ", "> ", "#", "***", "[", "](", "`", "**", "~~",
            "- [", "- [ ", "1.", "|a|\n|-|\n|", "> > nested", " ",
        ];

        foreach (var text in hostile)
        {
            _ = MarkdownReader.Read(text);
        }
    }
}
