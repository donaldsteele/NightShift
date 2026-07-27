using NightShift.Core.Configuration;

namespace NightShift.Core.Usage;

/// <summary>
/// The gate figure and the window it came from (plan.md §4.3).
/// </summary>
/// <param name="Percent">
/// The utilization the threshold is compared against, or null when it is unknown. Null means
/// "unavailable", never zero — see <see cref="UsageMetricSelector.Select"/>.
/// </param>
/// <param name="WindowName">
/// Which window supplied <paramref name="Percent"/>, for display only. Null whenever
/// <paramref name="Percent"/> is. This exists because <see cref="UsageMetric.HighestOfAll"/> can be
/// driven by the Opus or Sonnet window, neither of which has a gauge: without naming the winner the
/// dashboard shows a number that matches nothing on screen.
/// </param>
public readonly record struct GateMetricReading(double? Percent, string? WindowName);

/// <summary>
/// Turns a snapshot into the single number the threshold is compared against (plan.md §4.3).
/// </summary>
public static class UsageMetricSelector
{
    /// <summary>Display name of the 5-hour session window.</summary>
    public const string FiveHourName = "5h";

    /// <summary>Display name of the 7-day window.</summary>
    public const string SevenDayName = "7d";

    /// <summary>Display name of the per-model 7-day Opus window.</summary>
    public const string SevenDayOpusName = "7d Opus";

    /// <summary>Display name of the per-model 7-day Sonnet window.</summary>
    public const string SevenDaySonnetName = "7d Sonnet";

    /// <summary>
    /// The utilization percentage for <paramref name="metric"/>, or null when the snapshot has
    /// nothing to say about it. Null must be treated as "unavailable", never as zero: a missing
    /// window means we do not know the quota, and guessing "plenty left" is how an unattended
    /// pilot burns a weekly cap.
    /// </summary>
    public static double? Select(this UsageSnapshot snapshot, UsageMetric metric) =>
        SelectDetailed(snapshot, metric).Percent;

    /// <summary>
    /// The same figure as <see cref="Select"/>, plus the name of the window it came from.
    /// </summary>
    /// <remarks>
    /// Kept in one method with <see cref="Select"/> — rather than a parallel "which window won?"
    /// helper — so the name can never drift from the number the scheduler actually gated on.
    /// </remarks>
    public static GateMetricReading SelectDetailed(this UsageSnapshot snapshot, UsageMetric metric)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!snapshot.IsAvailable)
        {
            return default;
        }

        return metric switch
        {
            UsageMetric.FiveHour => Single(snapshot.FiveHour, FiveHourName),
            UsageMetric.SevenDay => Single(snapshot.SevenDay, SevenDayName),
            UsageMetric.HighestOfAll => HighestDetailed(snapshot),
            _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, "Unknown usage metric"),
        };

        static GateMetricReading Single(UsageWindow? window, string name) =>
            window is null ? default : new GateMetricReading(window.UtilizationPercent, name);
    }

    /// <summary>The largest of every window the provider actually reported.</summary>
    public static double? Highest(UsageSnapshot snapshot) => HighestDetailed(snapshot).Percent;

    /// <summary>The largest reported window, and which one it was.</summary>
    public static GateMetricReading HighestDetailed(UsageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var highest = default(GateMetricReading);
        foreach (var (window, name) in NamedWindows(snapshot))
        {
            if (window is not null && (highest.Percent is null || window.UtilizationPercent > highest.Percent))
            {
                highest = new GateMetricReading(window.UtilizationPercent, name);
            }
        }

        return highest;
    }

    /// <summary>
    /// The soonest reset among the windows at or above <paramref name="thresholdPercent"/> — what the
    /// scheduler reschedules against when a cycle is blocked (plan.md §6, step 5).
    /// </summary>
    public static DateTimeOffset? EarliestResetAtOrAbove(UsageSnapshot snapshot, double thresholdPercent)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        DateTimeOffset? earliest = null;
        foreach (var window in Windows(snapshot))
        {
            if (window is { ResetsAt: { } resetsAt }
                && window.UtilizationPercent >= thresholdPercent
                && (earliest is null || resetsAt < earliest))
            {
                earliest = resetsAt;
            }
        }

        return earliest;
    }

    /// <summary>
    /// The soonest window reset still in the future, whatever its utilization. This is what the
    /// scheduler anchors to: a window rolling over is the moment the most quota becomes available,
    /// so a tick placed just after it does the most work per slot.
    /// </summary>
    public static DateTimeOffset? EarliestFutureReset(UsageSnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        DateTimeOffset? earliest = null;
        foreach (var window in Windows(snapshot))
        {
            if (window is { ResetsAt: { } resetsAt }
                && resetsAt > now
                && (earliest is null || resetsAt < earliest))
            {
                earliest = resetsAt;
            }
        }

        return earliest;
    }

    static IEnumerable<UsageWindow?> Windows(UsageSnapshot snapshot)
    {
        foreach (var (window, _) in NamedWindows(snapshot))
        {
            yield return window;
        }
    }

    /// <summary>
    /// Every window a provider can report, paired with its display name. The single source of truth
    /// for what <see cref="UsageMetric.HighestOfAll"/> maximises over.
    /// </summary>
    public static IEnumerable<(UsageWindow? Window, string Name)> NamedWindows(UsageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        yield return (snapshot.FiveHour, FiveHourName);
        yield return (snapshot.SevenDay, SevenDayName);
        yield return (snapshot.SevenDayOpus, SevenDayOpusName);
        yield return (snapshot.SevenDaySonnet, SevenDaySonnetName);
    }
}
