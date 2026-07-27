using System.Globalization;

namespace NightShift.Desktop.Controls;

/// <summary>
/// Formats the relative times the Dashboard shows: the "resets in 2h 14m" line under each usage
/// gauge and the "Running — 3m elapsed" status pill (plan.md §9.1).
/// </summary>
/// <remarks>
/// Nothing here reads the clock. Every entry point takes the current instant — a
/// <see cref="DateTimeOffset"/> or a <see cref="TimeProvider"/> — so the same inputs always produce
/// the same string, which is what lets this be exercised without freezing time globally.
/// </remarks>
public static class RelativeTimeFormatter
{
    /// <summary>
    /// Under this much time left the countdown reads "resets now" rather than a number. "resets in
    /// 0m" is worse than useless: it looks like a stale figure, and the difference between 0 and 59
    /// seconds never changes what a user does.
    /// </summary>
    public static readonly TimeSpan ImminentWindow = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The "resets in …" line for a window reset, or <c>null</c> when the provider did not report
    /// one.
    /// </summary>
    /// <param name="resetsAt">When the rate-limit window rolls over, if known.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>
    /// "resets in 2h 14m", "resets in 12m", "resets now", or <c>null</c> when
    /// <paramref name="resetsAt"/> is <c>null</c>. Null rather than a placeholder on purpose: the
    /// caller should omit the line entirely rather than imply a countdown it does not have.
    /// </returns>
    public static string? FormatReset(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is not { } target)
        {
            return null;
        }

        var remaining = target - now;

        // A reset in the past means the window has already rolled over and the snapshot is simply
        // older than the reset it quoted — still "now", never a negative countdown.
        return remaining < ImminentWindow
            ? "resets now"
            : "resets in " + FormatDuration(remaining);
    }

    /// <summary>
    /// <see cref="FormatReset(DateTimeOffset?, DateTimeOffset)"/> against a
    /// <see cref="TimeProvider"/>, matching how the rest of the app takes its clock.
    /// </summary>
    public static string? FormatReset(DateTimeOffset? resetsAt, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        return FormatReset(resetsAt, timeProvider.GetUtcNow());
    }

    /// <summary>
    /// A compact duration: "45s", "12m", "1h 04m", "2d 03h". Negative spans are treated as zero.
    /// </summary>
    /// <remarks>
    /// Two units at most, and the second one is zero-padded so the width stays stable while a
    /// countdown ticks — a label that changes width every minute reads as flicker.
    /// </remarks>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        var culture = CultureInfo.InvariantCulture;

        if (duration.TotalDays >= 1)
        {
            return string.Create(culture, $"{(int)duration.TotalDays}d {duration.Hours:00}h");
        }

        if (duration.TotalHours >= 1)
        {
            return string.Create(culture, $"{(int)duration.TotalHours}h {duration.Minutes:00}m");
        }

        if (duration.TotalMinutes >= 1)
        {
            return string.Create(culture, $"{(int)duration.TotalMinutes}m");
        }

        return string.Create(culture, $"{duration.Seconds}s");
    }

    /// <summary>
    /// The elapsed-time half of the status pill: "3m elapsed", "1h 04m elapsed" (plan.md §9.1).
    /// </summary>
    public static string FormatElapsed(TimeSpan elapsed) => FormatDuration(elapsed) + " elapsed";

    /// <summary>
    /// The elapsed time of a run that started at <paramref name="startedAt"/>.
    /// </summary>
    public static string FormatElapsed(DateTimeOffset startedAt, DateTimeOffset now) =>
        FormatElapsed(now - startedAt);
}
