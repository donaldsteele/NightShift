using NightShift.Desktop.Controls;

namespace NightShift.Desktop.ViewModels;

/// <summary>
/// The view models' entry point to the relative-duration wording used by the status pill, the gauge
/// sub-captions and the history grid (plan.md §9.1: <c>next run in 42m</c>, <c>3m elapsed</c>,
/// <c>resets in 2h 14m</c>).
/// </summary>
/// <remarks>
/// <para>
/// The formatting rules themselves live in <see cref="RelativeTimeFormatter"/>, which ships with the
/// <c>UsageGauge</c> control. This type deliberately does not reimplement them: two formatters in
/// one window would eventually disagree, and "1h 4m" beside "1h 04m" is exactly the kind of
/// inconsistency nobody notices until it is everywhere.
/// </para>
/// <para>
/// What is added here is the one shape the formatter does not cover — a countdown to an absolute
/// instant, worded <c>in 42m</c> / <c>now</c>, which is what the status pill and the tray tooltip
/// need.
/// </para>
/// </remarks>
public static class TimeSpanText
{
    /// <summary>
    /// A compact duration: <c>45s</c>, <c>42m</c>, <c>2h 14m</c>, <c>3d 04h</c>. Negative and zero
    /// durations come back as <c>0s</c> — callers that need "overdue" wording decide that
    /// themselves, because "in -3m" is never the right answer.
    /// </summary>
    public static string Describe(TimeSpan value) => RelativeTimeFormatter.FormatDuration(value);

    /// <summary>
    /// <c>in 42m</c> / <c>now</c>. For anything scheduled in the future; a time that has already
    /// passed reads as <c>now</c> rather than as a negative countdown.
    /// </summary>
    public static string DescribeUntil(DateTimeOffset target, DateTimeOffset now)
    {
        var remaining = target - now;
        return remaining < RelativeTimeFormatter.ImminentWindow
            ? "now"
            : "in " + Describe(remaining);
    }

    /// <summary>
    /// A run's wall-clock length for the status pill and the history grid: <c>3m</c>,
    /// <c>1h 05m</c>. Under a minute reads in seconds, because a run that has been going eight
    /// seconds showing "0m elapsed" looks broken.
    /// </summary>
    public static string DescribeElapsed(TimeSpan value) => RelativeTimeFormatter.FormatDuration(value);
}
