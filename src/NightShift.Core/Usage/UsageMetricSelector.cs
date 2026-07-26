using NightShift.Core.Configuration;

namespace NightShift.Core.Usage;

/// <summary>
/// Turns a snapshot into the single number the threshold is compared against (plan.md §4.3).
/// </summary>
public static class UsageMetricSelector
{
    /// <summary>
    /// The utilization percentage for <paramref name="metric"/>, or null when the snapshot has
    /// nothing to say about it. Null must be treated as "unavailable", never as zero: a missing
    /// window means we do not know the quota, and guessing "plenty left" is how an unattended
    /// pilot burns a weekly cap.
    /// </summary>
    public static double? Select(this UsageSnapshot snapshot, UsageMetric metric)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!snapshot.IsAvailable)
        {
            return null;
        }

        return metric switch
        {
            UsageMetric.FiveHour => snapshot.FiveHour?.UtilizationPercent,
            UsageMetric.SevenDay => snapshot.SevenDay?.UtilizationPercent,
            UsageMetric.HighestOfAll => Highest(snapshot),
            _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, "Unknown usage metric"),
        };
    }

    /// <summary>The largest of every window the provider actually reported.</summary>
    public static double? Highest(UsageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        double? highest = null;
        foreach (var window in Windows(snapshot))
        {
            if (window is not null && (highest is null || window.UtilizationPercent > highest))
            {
                highest = window.UtilizationPercent;
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

    static IEnumerable<UsageWindow?> Windows(UsageSnapshot snapshot)
    {
        yield return snapshot.FiveHour;
        yield return snapshot.SevenDay;
        yield return snapshot.SevenDayOpus;
        yield return snapshot.SevenDaySonnet;
    }
}
