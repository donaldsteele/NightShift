using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using NightShift.Core.Markdown;

namespace NightShift.Desktop.Controls;

/// <summary>
/// Renders a <see cref="MarkdownDocument"/> as a panel of controls.
/// </summary>
/// <remarks>
/// <para>
/// Built in code rather than XAML, following <see cref="UsageGauge"/>. The structure is recursive
/// and heterogeneous — a quote holds blocks, a list item holds blocks, a table cell holds inlines —
/// and expressing that as nested XAML templates would mean nine of them, each needing an
/// <c>x:DataType</c> because the project compiles bindings by default. There is nothing to bind
/// here: the model is immutable and the tree is rebuilt whole when it changes.
/// </para>
/// <para>
/// Everything visual comes from the vocabulary in <c>App.axaml</c> — the same brushes, the same
/// <c>Border.code</c> and <c>Border.pill</c> and <c>Button.link</c> the dashboard uses — so a plan
/// looks like part of this app rather than like a browser embedded in it.
/// </para>
/// </remarks>
public sealed class MarkdownView : UserControl
{
    /// <summary>The document to draw. Null renders nothing.</summary>
    public static readonly StyledProperty<MarkdownDocument?> DocumentProperty =
        AvaloniaProperty.Register<MarkdownView, MarkdownDocument?>(nameof(Document));

    /// <summary>Invoked with a link's destination when the user activates it.</summary>
    /// <remarks>
    /// A callback rather than a command because the only caller hands it straight to
    /// <c>IShellLauncher.OpenUrl</c>, which already refuses anything that is not http or https.
    /// </remarks>
    public Action<string>? OpenLink { get; set; }

    public MarkdownView()
    {
        Focusable = false;
    }

    public MarkdownDocument? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == DocumentProperty)
        {
            Rebuild();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Brushes are resolved from the application's theme dictionaries, which are only reachable
        // once there is a tree to look up through. A document set before attachment would otherwise
        // render unstyled.
        Rebuild();
    }

    void Rebuild()
    {
        if (Document is not { } document)
        {
            Content = null;
            return;
        }

        Content = Stack(document.Blocks, spacing: 10);
    }

    // ── Blocks ─────────────────────────────────────────────────────────────────────────────────

    StackPanel Stack(IEnumerable<MarkdownBlock> blocks, double spacing)
    {
        var panel = new StackPanel { Spacing = spacing };

        foreach (var block in blocks)
        {
            panel.Children.Add(Build(block));
        }

        return panel;
    }

    Control Build(MarkdownBlock block) => block switch
    {
        HeadingBlock heading => BuildHeading(heading),
        ParagraphBlock paragraph => Text(paragraph.Inlines),
        ListBlock list => BuildList(list),
        CodeBlock code => BuildCode(code),
        QuoteBlock quote => BuildQuote(quote),
        TableBlock table => BuildTable(table),
        RuleBlock => new Separator { Margin = new Thickness(0, 6) },
        _ => new Control(),
    };

    Control BuildHeading(HeadingBlock heading)
    {
        var text = Text(heading.Inlines);
        text.FontWeight = FontWeight.SemiBold;
        text.FontSize = heading.Level switch
        {
            1 => 22,
            2 => 18,
            3 => 15,
            _ => 13,
        };

        if (heading.Level >= 4)
        {
            text.Foreground = Brush("NsSubtleBrush");
        }

        text.Margin = new Thickness(0, heading.Level <= 2 ? 10 : 4, 0, 0);

        if (heading.Marker == MilestoneMarker.None)
        {
            return text;
        }

        // A milestone's status is a fact the dashboard already counts, so it is drawn as the same
        // pill the status strip uses rather than left as bold text in the middle of a sentence.
        var pill = new Border { Classes = { "pill", heading.Marker == MilestoneMarker.Delivered ? "running" : "blocked" } };
        pill.Child = new TextBlock
        {
            Text = heading.Marker == MilestoneMarker.Delivered ? "delivered" : "blocked",
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = text.Margin,
        };
        text.Margin = default;
        row.Children.Add(text);
        row.Children.Add(pill);
        return row;
    }

    Control BuildCode(CodeBlock code)
    {
        var body = new SelectableTextBlock
        {
            Classes = { "mono" },
            Text = code.Text,
            TextWrapping = TextWrapping.NoWrap,
        };

        var scroller = new ScrollViewer
        {
            Content = body,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var border = new Border { Classes = { "code" }, Child = scroller };

        if (code.Language is not { Length: > 0 })
        {
            return border;
        }

        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(new TextBlock { Classes = { "caption" }, Text = code.Language });
        panel.Children.Add(border);
        return panel;
    }

    Control BuildQuote(QuoteBlock quote)
    {
        var inner = Stack(quote.Blocks, spacing: 8);
        inner.Margin = new Thickness(12, 2, 0, 2);

        return new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = Brush("NsInfoBrush"),
            Child = inner,
        };
    }

    Control BuildList(ListBlock list)
    {
        var panel = new StackPanel { Spacing = 4 };
        var number = 1;

        foreach (var item in list.Items)
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            };

            var marker = item.Task is { } task ? TaskGlyph(task) : BulletGlyph(list.Ordered, number);
            marker.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(marker, 0);
            row.Children.Add(marker);

            var content = Stack(item.Blocks, spacing: 4);
            Grid.SetColumn(content, 1);
            row.Children.Add(content);

            // A finished item is dimmed, which is what makes a plan with four hundred items
            // skimmable: the eye lands on what is left rather than on what is done.
            if (item.Task == TaskMark.Done)
            {
                content.Opacity = 0.65;
            }

            panel.Children.Add(row);
            number++;
        }

        return panel;
    }

    TextBlock TaskGlyph(TaskMark task) => new()
    {
        Text = task switch
        {
            TaskMark.Done => "☑",
            TaskMark.Blocked => "⚠",
            _ => "☐",
        },
        FontSize = 14,
        Foreground = task switch
        {
            TaskMark.Done => Brush("NsOkBrush"),
            TaskMark.Blocked => Brush("NsCriticalBrush"),
            _ => Brush("NsSubtleBrush"),
        },
    };

    TextBlock BulletGlyph(bool ordered, int number) => new()
    {
        Text = ordered ? $"{number}." : "•",
        Foreground = Brush("NsSubtleBrush"),
    };

    Control BuildTable(TableBlock table)
    {
        var columns = Math.Max(table.Alignments.Count, table.Header.Count);
        if (columns == 0)
        {
            return new Control();
        }

        var grid = new Grid();

        // First column Auto, the rest star: one real table cell in these plans runs to about 1200
        // characters, and an all-Auto grid would push it off the side of the window.
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (var i = 1; i < columns; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }

        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        AddRow(grid, table, table.Header, row: 0, header: true);

        for (var r = 0; r < table.Rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            AddRow(grid, table, table.Rows[r], r + 1, header: false);
        }

        return new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = Brush("NsCardBorderBrush"),
            CornerRadius = new CornerRadius(4),
            Child = grid,
        };
    }

    void AddRow(Grid grid, TableBlock table, IReadOnlyList<TableCell> cells, int row, bool header)
    {
        for (var column = 0; column < grid.ColumnDefinitions.Count; column++)
        {
            var text = column < cells.Count ? Text(cells[column].Inlines) : new SelectableTextBlock();
            text.Margin = new Thickness(8, 5);
            text.TextAlignment = column < table.Alignments.Count
                ? table.Alignments[column] switch
                {
                    ColumnAlignment.Center => TextAlignment.Center,
                    ColumnAlignment.Right => TextAlignment.Right,
                    _ => TextAlignment.Left,
                }
                : TextAlignment.Left;

            if (header)
            {
                text.FontWeight = FontWeight.SemiBold;
            }

            var cell = new Border
            {
                Background = header ? Brush("NsCodeBrush") : null,
                BorderThickness = new Thickness(column == 0 ? 0 : 1, row == 0 ? 0 : 1, 0, 0),
                BorderBrush = Brush("NsCardBorderBrush"),
                Child = text,
            };

            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            grid.Children.Add(cell);
        }
    }

    // ── Inlines ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One <see cref="SelectableTextBlock"/> per block, however many runs it holds — so selecting
    /// and copying a paragraph works the way it does in the dashboard's output pane.
    /// </summary>
    SelectableTextBlock Text(IReadOnlyList<MarkdownInline> inlines)
    {
        var block = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };

        foreach (var inline in Inlines(inlines))
        {
            block.Inlines?.Add(inline);
        }

        return block;
    }

    IEnumerable<Inline> Inlines(IReadOnlyList<MarkdownInline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextRun text:
                    yield return new Run(text.Text);
                    break;

                case CodeRun code:
                    // A plain Run with a background rather than a control per span: this repo's
                    // own plan has 411 code spans, and a container each would be 411 extra
                    // controls to lay out.
                    yield return new Run(code.Text)
                    {
                        FontFamily = MonospaceFont,
                        Background = Brush("NsCodeBrush"),
                        Foreground = Brush("NsInfoBrush"),
                    };
                    break;

                case LinkRun link:
                    yield return LinkInline(link);
                    break;

                case StyledRun styled:
                    yield return StyledSpan(styled);
                    break;
            }
        }
    }

    Inline StyledSpan(StyledRun styled)
    {
        var span = new Span();

        foreach (var child in Inlines(styled.Inlines))
        {
            span.Inlines.Add(child);
        }

        switch (styled.Kind)
        {
            case StyleKind.Bold:
                span.FontWeight = FontWeight.SemiBold;
                break;
            case StyleKind.Italic:
                span.FontStyle = FontStyle.Italic;
                break;
            case StyleKind.BoldItalic:
                span.FontWeight = FontWeight.SemiBold;
                span.FontStyle = FontStyle.Italic;
                break;
            case StyleKind.Strikethrough:
                span.TextDecorations = TextDecorations.Strikethrough;
                break;
        }

        return span;
    }

    Inline LinkInline(LinkRun link)
    {
        var button = new Button
        {
            Classes = { "link" },
            Content = new TextBlock { Text = link.Text },
        };

        button.Click += (_, _) => OpenLink?.Invoke(link.Url);

        return new InlineUIContainer(button);
    }

    // ── Resources ──────────────────────────────────────────────────────────────────────────────

    FontFamily MonospaceFont =>
        this.TryFindResource("NsMonospace", out var value) && value is FontFamily family
            ? family
            : FontFamily.Default;

    /// <summary>
    /// Looks a themed brush up once, at build time. The app hard-codes its theme variant and offers
    /// no runtime switch, so re-resolving on a theme change would be code nothing can reach.
    /// </summary>
    IBrush? Brush(string key) =>
        this.TryFindResource(key, out var value) ? value as IBrush : null;
}
