using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace NightShift.Desktop.Controls;

/// <summary>
/// The Dashboard usage gauge (plan.md §9.1): a 270° arc with a large percentage label in the
/// middle, a caption ("Session (5h)") and a reset countdown ("resets in 2h 14m") underneath.
/// </summary>
/// <remarks>
/// <para>
/// Drawn in <see cref="Render"/> rather than templated. There is no control theme to keep in step
/// with the Fluent theme, no template parts to find, and the status colours can be stated
/// explicitly — which matters, because the whole point of the control is that a user reads the
/// right number and the right severity at a glance.
/// </para>
/// <para>
/// Two states are load-bearing rather than decorative. <see cref="IsUnavailable"/> draws a dash and
/// a dotted, obviously-empty ring: rendering 0% for "we could not read the quota" would read as
/// "plenty left", which is the exact misreading this app exists to avoid.
/// <see cref="IsApproximate"/> stamps a chip on the gauge, because the ccusage fallback has been
/// seen reporting 41% against a real 14% (plan.md §4) and an estimate must never be mistaken for
/// the account's true quota figure.
/// </para>
/// </remarks>
public sealed class UsageGauge : Control
{
    /// <summary>Where the arc starts, in degrees clockwise from 3 o'clock. 135° is 7-o'clock-ish.</summary>
    const double StartAngleDegrees = 135;

    /// <summary>How far the arc sweeps clockwise, leaving the gap at the bottom.</summary>
    const double TotalSweepDegrees = 270;

    /// <summary>Below this the arc is dropped and only the number is drawn — a 20px ring is noise.</summary>
    const double MinimumArcSide = 36;

    const double PreferredWidth = 168;
    const double PreferredHeight = 196;

    /// <summary>The utilization to show, 0–100. <see cref="double.NaN"/> is treated as unknown.</summary>
    /// <remarks>
    /// Out-of-range values are clamped for drawing only; nothing here rejects them, because a
    /// provider returning 103% should still light the gauge red rather than throw in a render pass.
    /// </remarks>
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<UsageGauge, double>(nameof(Value));

    /// <summary>Which window this gauge is for, e.g. "Session (5h)" or "Weekly (7d)".</summary>
    public static readonly StyledProperty<string?> CaptionProperty =
        AvaloniaProperty.Register<UsageGauge, string?>(nameof(Caption));

    /// <summary>The line under the caption, e.g. "resets in 2h 14m" (see <see cref="RelativeTimeFormatter"/>).</summary>
    public static readonly StyledProperty<string?> SubCaptionProperty =
        AvaloniaProperty.Register<UsageGauge, string?>(nameof(SubCaption));

    /// <summary>
    /// The red breakpoint — the same number as the usage gate's threshold (plan.md §9.2), so the
    /// gauge turns red exactly when the pilot would start skipping runs.
    /// </summary>
    public static readonly StyledProperty<double> ThresholdProperty =
        AvaloniaProperty.Register<UsageGauge, double>(nameof(Threshold), 90d);

    /// <summary>The amber breakpoint. A property, not a constant, so the two bands stay adjustable together.</summary>
    public static readonly StyledProperty<double> WarningThresholdProperty =
        AvaloniaProperty.Register<UsageGauge, double>(nameof(WarningThreshold), 70d);

    /// <summary>True when the figure is an estimate rather than the account's own quota number.</summary>
    public static readonly StyledProperty<bool> IsApproximateProperty =
        AvaloniaProperty.Register<UsageGauge, bool>(nameof(IsApproximate));

    /// <summary>True when no figure could be obtained at all — draws a dash, never a zero.</summary>
    public static readonly StyledProperty<bool> IsUnavailableProperty =
        AvaloniaProperty.Register<UsageGauge, bool>(nameof(IsUnavailable));

    static UsageGauge()
    {
        AffectsRender<UsageGauge>(
            ValueProperty,
            CaptionProperty,
            SubCaptionProperty,
            ThresholdProperty,
            WarningThresholdProperty,
            IsApproximateProperty,
            IsUnavailableProperty);

        // The chip and the two text lines are part of the control's desired height.
        AffectsMeasure<UsageGauge>(CaptionProperty, SubCaptionProperty, IsApproximateProperty);
    }

    /// <summary>Creates the gauge and keeps it in step with light/dark switches at runtime.</summary>
    public UsageGauge()
    {
        // Colours are picked per theme variant in Render, so a variant switch has to repaint; it is
        // not an Avalonia property change that AffectsRender would catch.
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    /// <inheritdoc cref="ValueProperty"/>
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <inheritdoc cref="CaptionProperty"/>
    public string? Caption
    {
        get => GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    /// <inheritdoc cref="SubCaptionProperty"/>
    public string? SubCaption
    {
        get => GetValue(SubCaptionProperty);
        set => SetValue(SubCaptionProperty, value);
    }

    /// <inheritdoc cref="ThresholdProperty"/>
    public double Threshold
    {
        get => GetValue(ThresholdProperty);
        set => SetValue(ThresholdProperty, value);
    }

    /// <inheritdoc cref="WarningThresholdProperty"/>
    public double WarningThreshold
    {
        get => GetValue(WarningThresholdProperty);
        set => SetValue(WarningThresholdProperty, value);
    }

    /// <inheritdoc cref="IsApproximateProperty"/>
    public bool IsApproximate
    {
        get => GetValue(IsApproximateProperty);
        set => SetValue(IsApproximateProperty, value);
    }

    /// <inheritdoc cref="IsUnavailableProperty"/>
    public bool IsUnavailable
    {
        get => GetValue(IsUnavailableProperty);
        set => SetValue(IsUnavailableProperty, value);
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize) =>
        new(
            Math.Min(PreferredWidth, availableSize.Width),
            Math.Min(PreferredHeight, availableSize.Height));

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);

        if (change.Property == IsApproximateProperty || change.Property == IsUnavailableProperty)
        {
            UpdateQualifierTip();
        }
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        base.Render(context);

        var size = Bounds.Size;
        if (size.Width < 8 || size.Height < 8 || double.IsNaN(size.Width) || double.IsNaN(size.Height))
        {
            // Mid-layout or collapsed to nothing. Anything drawn here would be a fragment of a
            // gauge, which is more misleading than an empty box.
            return;
        }

        var palette = GaugePalette.For(ActualThemeVariant);
        var percent = ClampPercent(Value);
        var unknown = IsUnavailable || double.IsNaN(percent);
        var statusBrush = unknown
            ? palette.Muted
            : percent >= Threshold ? palette.Critical
            : percent >= WarningThreshold ? palette.Warning
            : palette.Ok;

        using var clip = context.PushClip(new Rect(size));

        var gap = Math.Clamp(size.Height * 0.025, 2, 7);
        var caption = CreateText(Caption, Math.Clamp(size.Width * 0.10, 10, 14), FontWeight.SemiBold, unknown ? palette.Muted : palette.Text, size.Width);
        var subCaption = CreateText(SubCaption, Math.Clamp(size.Width * 0.085, 9, 12), FontWeight.Normal, palette.Muted, size.Width);
        var chip = IsApproximate ? CreateChipText(palette, size.Width) : null;
        var chipPadding = chip is null ? default : new Size(Math.Max(4, chip.Height * 0.45), Math.Max(1, chip.Height * 0.15));

        // Stack from the bottom up: arc, then chip, caption and countdown. When the control is too
        // short for all of it the countdown goes first and the caption second — the chip never
        // does, because losing it would turn an estimate into an apparent fact.
        var belowHeight = Below();
        while (size.Height - belowHeight < MinimumArcSide && subCaption is not null)
        {
            subCaption = null;
            belowHeight = Below();
        }

        while (size.Height - belowHeight < MinimumArcSide && caption is not null)
        {
            caption = null;
            belowHeight = Below();
        }

        var arcSide = Math.Min(size.Width, size.Height - belowHeight);
        Rect numberArea;

        if (arcSide >= MinimumArcSide)
        {
            // Centre the whole stack: when the arc is width-limited the leftover height belongs
            // half above and half below, not all below.
            var top = Math.Max(0, (size.Height - (arcSide + belowHeight)) / 2);
            numberArea = new Rect((size.Width - arcSide) / 2, top, arcSide, arcSide);
            DrawArc(context, numberArea, palette, statusBrush, percent, Threshold, unknown);
        }
        else
        {
            // No room for a ring; the number alone still tells the truth.
            numberArea = new Rect(0, 0, size.Width, Math.Max(0, size.Height - belowHeight));
        }

        DrawReading(context, numberArea, statusBrush, percent, unknown);

        // The 270° arc opens at the bottom, so the first line below it can climb into that gap
        // without colliding with the stroke.
        var y = numberArea.Bottom - (arcSide >= MinimumArcSide ? arcSide * 0.07 : 0) + gap;
        y = DrawChip(context, chip, chipPadding, palette, size.Width, y) + gap;
        y = DrawCentred(context, caption, size.Width, y) + gap;
        DrawCentred(context, subCaption, size.Width, y);

        double Below()
        {
            var height = 0d;
            if (chip is not null)
            {
                height += chip.Height + (chipPadding.Height * 2) + gap;
            }

            if (caption is not null)
            {
                height += caption.Height + gap;
            }

            if (subCaption is not null)
            {
                height += subCaption.Height + gap;
            }

            return height;
        }
    }

    /// <summary>
    /// Clamps to the drawable 0–100 range, passing <see cref="double.NaN"/> through untouched so it
    /// can be reported as unknown rather than silently becoming 0%.
    /// </summary>
    static double ClampPercent(double value) => double.IsNaN(value) ? double.NaN : Math.Clamp(value, 0, 100);

    /// <summary>Draws the track ring, the filled value ring and the threshold tick.</summary>
    static void DrawArc(
        DrawingContext context,
        Rect area,
        GaugePalette palette,
        IImmutableSolidColorBrush statusBrush,
        double percent,
        double thresholdPercent,
        bool unknown)
    {
        var stroke = Math.Clamp(area.Width * 0.11, 3, 16);
        var radius = (area.Width - stroke) / 2;
        if (radius <= 0)
        {
            return;
        }

        var centre = area.Center;

        // Unknown gets a dotted track: an empty solid ring still looks like a gauge reading zero.
        var trackPen = unknown
            ? new Pen(palette.Track, stroke, new DashStyle([0.05, 1.6], 0), PenLineCap.Round)
            : new Pen(palette.Track, stroke, lineCap: PenLineCap.Round);

        context.DrawGeometry(null, trackPen, ArcGeometry(centre, radius, StartAngleDegrees, TotalSweepDegrees));

        if (!unknown)
        {
            var sweep = TotalSweepDegrees * (percent / 100);
            if (sweep >= 0.5)
            {
                context.DrawGeometry(null, new Pen(statusBrush, stroke, lineCap: PenLineCap.Round), ArcGeometry(centre, radius, StartAngleDegrees, sweep));
            }

            // A notch where the gate trips, so "how close am I to being skipped" is readable
            // without doing arithmetic against the label.
            if (area.Width >= 72 && thresholdPercent is > 0 and <= 100)
            {
                var tickDegrees = StartAngleDegrees + (TotalSweepDegrees * (thresholdPercent / 100));
                var inner = PointOnCircle(centre, radius - (stroke / 2) + 1, tickDegrees);
                var outer = PointOnCircle(centre, radius + (stroke / 2) - 1, tickDegrees);
                context.DrawLine(new Pen(palette.Tick, Math.Max(1, stroke * 0.14)), inner, outer);
            }
        }
    }

    /// <summary>The big number, or an em dash when there is nothing honest to put there.</summary>
    static void DrawReading(DrawingContext context, Rect area, IImmutableSolidColorBrush brush, double percent, bool unknown)
    {
        if (area.Height < 12 || area.Width < 12)
        {
            return;
        }

        var fontSize = Math.Clamp(Math.Min(area.Width * 0.28, area.Height * 0.34), 9, 56);
        var text = unknown
            ? "—"
            : string.Create(CultureInfo.InvariantCulture, $"{percent:0}%");

        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold),
            fontSize,
            brush);

        context.DrawText(formatted, new Point(area.Center.X - (formatted.Width / 2), area.Center.Y - (formatted.Height / 2)));
    }

    /// <summary>Draws the "approximate" chip and returns the y coordinate under it.</summary>
    static double DrawChip(DrawingContext context, FormattedText? chip, Size padding, GaugePalette palette, double width, double y)
    {
        if (chip is null)
        {
            return y;
        }

        var chipWidth = Math.Min(width, chip.Width + (padding.Width * 2));
        var chipHeight = chip.Height + (padding.Height * 2);
        var rect = new Rect((width - chipWidth) / 2, y, chipWidth, chipHeight);
        var radius = chipHeight / 2;

        context.DrawRectangle(palette.ChipFill, new Pen(palette.ChipBorder, 1), rect, radius, radius);
        context.DrawText(chip, new Point(rect.Center.X - (chip.Width / 2), rect.Y + padding.Height));

        return rect.Bottom;
    }

    /// <summary>Draws a text line centred horizontally and returns the y coordinate under it.</summary>
    static double DrawCentred(DrawingContext context, FormattedText? text, double width, double y)
    {
        if (text is null)
        {
            return y;
        }

        context.DrawText(text, new Point((width - text.Width) / 2, y));
        return y + text.Height;
    }

    /// <summary>Null for null/blank input, so callers can leave the countdown out entirely.</summary>
    static FormattedText? CreateText(string? text, double fontSize, FontWeight weight, IImmutableSolidColorBrush brush, double maxWidth)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, weight),
            fontSize,
            brush)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
    }

    /// <summary>
    /// The chip label, shortened before it is ever ellipsised: "approx…" would look like a
    /// truncation artefact rather than a warning.
    /// </summary>
    static FormattedText CreateChipText(GaugePalette palette, double width)
    {
        var fontSize = Math.Clamp(width * 0.075, 8, 11);
        var full = CreateText("approximate", fontSize, FontWeight.SemiBold, palette.ChipText, width)!;

        return full.Width + (fontSize * 2) <= width
            ? full
            : CreateText("approx", fontSize, FontWeight.SemiBold, palette.ChipText, width)!;
    }

    /// <summary>An open arc from <paramref name="startDegrees"/> sweeping clockwise.</summary>
    static StreamGeometry ArcGeometry(Point centre, double radius, double startDegrees, double sweepDegrees)
    {
        var geometry = new StreamGeometry();

        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(PointOnCircle(centre, radius, startDegrees), isFilled: false);
            ctx.ArcTo(
                PointOnCircle(centre, radius, startDegrees + sweepDegrees),
                new Size(radius, radius),
                rotationAngle: 0,
                isLargeArc: sweepDegrees > 180,
                SweepDirection.Clockwise);
            ctx.EndFigure(isClosed: false);
        }

        return geometry;
    }

    /// <summary>Screen coordinates (y grows downward), so increasing degrees runs clockwise.</summary>
    static Point PointOnCircle(Point centre, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(centre.X + (radius * Math.Cos(radians)), centre.Y + (radius * Math.Sin(radians)));
    }

    /// <summary>
    /// Spells out what the chip and the dash mean on hover. The gauge has room for one word; the
    /// reason an estimate cannot be trusted needs a sentence.
    /// </summary>
    void UpdateQualifierTip()
    {
        object? tip = null;

        if (IsUnavailable)
        {
            tip = "Usage could not be read. This is not a quota figure — the pilot treats it as unknown, not as zero.";
        }
        else if (IsApproximate)
        {
            tip = "Estimated from local ccusage data rather than the account's quota endpoint, and it can be badly out (41% reported against a real 14%). Treat it as a hint.";
        }

        ToolTip.SetTip(this, tip);
    }

    /// <summary>
    /// The drawing colours for one theme variant. Stated explicitly rather than resolved from theme
    /// resources: the status colours have to stay recognisable as green/amber/red and legible on
    /// whatever the host paints behind them, which a themed accent brush cannot promise.
    /// </summary>
    sealed class GaugePalette
    {
        static readonly GaugePalette LightVariant = new()
        {
            Text = Brush("#1F2328"),
            Muted = Brush("#6B7280"),
            Track = Brush("#E4E4E7"),
            Tick = Brush("#9CA3AF"),
            Ok = Brush("#15803D"),
            Warning = Brush("#B45309"),
            Critical = Brush("#B91C1C"),
            ChipText = Brush("#92400E"),
            ChipFill = Brush("#33F59E0B"),
            ChipBorder = Brush("#88B45309"),
        };

        static readonly GaugePalette DarkVariant = new()
        {
            Text = Brush("#F4F4F5"),
            Muted = Brush("#A1A1AA"),
            Track = Brush("#3F3F46"),
            Tick = Brush("#71717A"),
            Ok = Brush("#4ADE80"),
            Warning = Brush("#FBBF24"),
            Critical = Brush("#F87171"),
            ChipText = Brush("#FCD34D"),
            ChipFill = Brush("#33F59E0B"),
            ChipBorder = Brush("#88FBBF24"),
        };

        public required IImmutableSolidColorBrush Text { get; init; }

        public required IImmutableSolidColorBrush Muted { get; init; }

        public required IImmutableSolidColorBrush Track { get; init; }

        public required IImmutableSolidColorBrush Tick { get; init; }

        public required IImmutableSolidColorBrush Ok { get; init; }

        public required IImmutableSolidColorBrush Warning { get; init; }

        public required IImmutableSolidColorBrush Critical { get; init; }

        public required IImmutableSolidColorBrush ChipText { get; init; }

        public required IImmutableSolidColorBrush ChipFill { get; init; }

        public required IImmutableSolidColorBrush ChipBorder { get; init; }

        /// <summary>Dark palette for the dark variant, light palette for everything else.</summary>
        public static GaugePalette For(ThemeVariant? variant) =>
            variant == ThemeVariant.Dark ? DarkVariant : LightVariant;

        static ImmutableSolidColorBrush Brush(string color) => new(Color.Parse(color));
    }
}
