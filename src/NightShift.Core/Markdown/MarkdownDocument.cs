namespace NightShift.Core.Markdown;

/// <summary>A parsed plan file: an ordered list of blocks, and nothing else.</summary>
/// <remarks>
/// <para>
/// Immutable, BCL-only and framework-free, so it parses off the UI thread and unit-tests the way
/// <see cref="Preflight.PlanParser"/> does. The Desktop renderer walks this; nothing in here knows
/// what a control is.
/// </para>
/// <para>
/// <b>Why this exists at all rather than a NuGet package.</b> No free stable markdown renderer
/// targets Avalonia 12, and more to the point none of them know NightShift's own vocabulary:
/// <c>- [!]</c> is this app's invention for a blocked item, and <c>— **delivered 2026-07-27**</c>
/// on a milestone heading is a fact the dashboard already counts. A general renderer prints both
/// as literal text. Here they are <see cref="TaskMark.Blocked"/> and
/// <see cref="MilestoneMarker.Delivered"/> — things the view can draw as a red mark and a green
/// pill.
/// </para>
/// </remarks>
public sealed record MarkdownDocument(IReadOnlyList<MarkdownBlock> Blocks)
{
    /// <summary>A document with no blocks — what empty or whitespace-only text parses to.</summary>
    public static MarkdownDocument Empty { get; } = new([]);
}

// ── Blocks ─────────────────────────────────────────────────────────────────────────────────────

/// <summary>One top-level piece of a document.</summary>
public abstract record MarkdownBlock;

/// <summary>An ATX heading, <c>#</c> through <c>######</c>.</summary>
/// <param name="Level">1-6, the number of leading hashes.</param>
/// <param name="MilestoneNumber">
/// The <c>7</c> in <c>### M7 — Title</c>, or null for an ordinary heading. Recognised with the
/// same pattern the dashboard tally uses, so the two can never disagree about what a milestone is.
/// </param>
public sealed record HeadingBlock(
    int Level,
    IReadOnlyList<MarkdownInline> Inlines,
    int? MilestoneNumber,
    MilestoneMarker Marker) : MarkdownBlock;

/// <summary>A run of prose. Hard-wrapped source lines are joined with single spaces.</summary>
public sealed record ParagraphBlock(IReadOnlyList<MarkdownInline> Inlines) : MarkdownBlock;

/// <summary>A bullet or numbered list.</summary>
public sealed record ListBlock(bool Ordered, IReadOnlyList<ListItem> Items) : MarkdownBlock;

/// <summary>
/// One item. <paramref name="Blocks"/> rather than inlines because an item can hold a nested list
/// or a second paragraph, and flattening that would lose the plan's structure.
/// </summary>
/// <param name="Task">
/// Set when the item is a task item — <c>- [ ]</c>, <c>- [x]</c> or NightShift's <c>- [!]</c>.
/// Null for an ordinary bullet.
/// </param>
public sealed record ListItem(TaskMark? Task, IReadOnlyList<MarkdownBlock> Blocks);

/// <summary>A fenced code block. Its text is verbatim and is never parsed again.</summary>
/// <param name="Language">The info string, or null when the fence carried none.</param>
public sealed record CodeBlock(string? Language, string Text) : MarkdownBlock;

/// <summary>A block quote. Recursive, because this repo's plan puts lists and tables inside them.</summary>
public sealed record QuoteBlock(IReadOnlyList<MarkdownBlock> Blocks) : MarkdownBlock;

/// <summary>A pipe table with a header row and an alignment row.</summary>
public sealed record TableBlock(
    IReadOnlyList<ColumnAlignment> Alignments,
    IReadOnlyList<TableCell> Header,
    IReadOnlyList<IReadOnlyList<TableCell>> Rows) : MarkdownBlock;

/// <summary>One table cell's contents.</summary>
public sealed record TableCell(IReadOnlyList<MarkdownInline> Inlines);

/// <summary>A thematic break — <c>---</c>, <c>***</c> or <c>___</c> on its own line.</summary>
public sealed record RuleBlock : MarkdownBlock;

// ── Inlines ────────────────────────────────────────────────────────────────────────────────────

/// <summary>One span within a block's text.</summary>
public abstract record MarkdownInline;

/// <summary>Literal text. Anything the reader did not recognise ends up here.</summary>
public sealed record TextRun(string Text) : MarkdownInline;

/// <summary>Emphasised text. Nests, so <c>**bold with `code`**</c> keeps the code span.</summary>
public sealed record StyledRun(StyleKind Kind, IReadOnlyList<MarkdownInline> Inlines) : MarkdownInline;

/// <summary>
/// A backtick code span. Its text is literal and is never parsed further — which is what keeps
/// <c>`&lt;Nullable&gt;enable&lt;/Nullable&gt;`</c> and <c>`--token-limit &lt;n|max&gt;`</c> intact.
/// Every angle bracket in this repo's own plan file is inside one of these.
/// </summary>
public sealed record CodeRun(string Text) : MarkdownInline;

/// <summary>An inline link. Only <c>http</c> and <c>https</c> are ever opened, by the shell layer.</summary>
public sealed record LinkRun(string Text, string Url) : MarkdownInline;

// ── Enums ──────────────────────────────────────────────────────────────────────────────────────

/// <summary>The three marks a plan task item can carry.</summary>
public enum TaskMark
{
    /// <summary><c>- [ ]</c> — open work.</summary>
    Todo,

    /// <summary><c>- [x]</c>.</summary>
    Done,

    /// <summary>
    /// <c>- [!]</c> — NightShift's own mark, written by a run that hit a wall. No general markdown
    /// renderer knows it; drawing it as a mark rather than the literal text <c>[!]</c> is most of
    /// the reason this reader exists.
    /// </summary>
    Blocked,
}

/// <summary>What a milestone heading says about itself.</summary>
/// <remarks>
/// Read from the heading line only. A milestone whose delivery is recorded in a body
/// <c>**Status:**</c> line reports <see cref="None"/> here even though
/// <see cref="Preflight.PlanParser"/> counts it delivered — the tally reads a whole milestone's
/// body, a heading knows only itself. The window states the tally beside the document rather than
/// trying to reconcile the two silently.
/// </remarks>
public enum MilestoneMarker
{
    /// <summary>No marker on the heading.</summary>
    None,

    /// <summary><c>— **delivered 2026-07-27**</c>.</summary>
    Delivered,

    /// <summary><c>**Blocked:**</c>. Beats <see cref="Delivered"/>, as it does in the tally.</summary>
    Blocked,
}

/// <summary>Which emphasis a <see cref="StyledRun"/> carries.</summary>
public enum StyleKind
{
    Bold,
    Italic,
    BoldItalic,
    Strikethrough,
}

/// <summary>A table column's alignment, from the <c>:---:</c> row.</summary>
public enum ColumnAlignment
{
    /// <summary><c>---</c> — no colon, so the renderer picks.</summary>
    Default,

    /// <summary><c>:---</c>.</summary>
    Left,

    /// <summary><c>:---:</c>.</summary>
    Center,

    /// <summary><c>---:</c>.</summary>
    Right,
}
