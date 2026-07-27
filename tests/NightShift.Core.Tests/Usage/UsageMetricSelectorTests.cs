using NightShift.Core.Configuration;
using NightShift.Core.Usage;

namespace NightShift.Core.Tests.Usage;

public sealed class UsageMetricSelectorTests
{
    static readonly DateTimeOffset Now = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    static UsageSnapshot Snapshot(
        double? fiveHour = null,
        double? sevenDay = null,
        double? opus = null,
        double? sonnet = null,
        DateTimeOffset? fiveHourReset = null,
        DateTimeOffset? sevenDayReset = null) =>
        new(
            fiveHour is { } f ? new UsageWindow(f, fiveHourReset) : null,
            sevenDay is { } s ? new UsageWindow(s, sevenDayReset) : null,
            opus is { } o ? new UsageWindow(o, null) : null,
            sonnet is { } n ? new UsageWindow(n, null) : null,
            UsageSource.OAuth,
            IsApproximate: false,
            Now);

    [Fact]
    public void FiveHour_selects_only_the_session_window() =>
        Assert.Equal(33, Snapshot(fiveHour: 33, sevenDay: 90).Select(UsageMetric.FiveHour));

    [Fact]
    public void SevenDay_selects_only_the_weekly_window() =>
        Assert.Equal(90, Snapshot(fiveHour: 33, sevenDay: 90).Select(UsageMetric.SevenDay));

    [Fact]
    public void HighestOfAll_blocks_when_only_the_weekly_window_is_hot() =>
        Assert.Equal(94, Snapshot(fiveHour: 12, sevenDay: 94, sonnet: 3).Select(UsageMetric.HighestOfAll));

    [Fact]
    public void HighestOfAll_includes_the_per_model_windows() =>
        Assert.Equal(77, Snapshot(fiveHour: 12, sevenDay: 20, opus: 77).Select(UsageMetric.HighestOfAll));

    [Fact]
    public void A_missing_window_is_null_not_zero()
    {
        // A fabricated 0 would read as "plenty of quota left" and start a run.
        Assert.Null(Snapshot(sevenDay: 50).Select(UsageMetric.FiveHour));
        Assert.Null(Snapshot().Select(UsageMetric.HighestOfAll));
    }

    [Fact]
    public void An_unavailable_snapshot_selects_nothing() =>
        Assert.Null(UsageSnapshot.Unavailable("no providers", Now).Select(UsageMetric.HighestOfAll));

    [Fact]
    public void The_detailed_reading_names_the_window_that_won()
    {
        // The dashboard shows this name because neither per-model window has a gauge: without it a
        // gate blocked by Opus is a number that matches nothing on screen.
        var reading = Snapshot(fiveHour: 12, sevenDay: 20, opus: 77).SelectDetailed(UsageMetric.HighestOfAll);

        Assert.Equal(77, reading.Percent);
        Assert.Equal(UsageMetricSelector.SevenDayOpusName, reading.WindowName);
    }

    [Theory]
    [InlineData(UsageMetric.FiveHour, 33, UsageMetricSelector.FiveHourName)]
    [InlineData(UsageMetric.SevenDay, 90, UsageMetricSelector.SevenDayName)]
    public void A_single_window_metric_names_that_window(UsageMetric metric, double expected, string name)
    {
        var reading = Snapshot(fiveHour: 33, sevenDay: 90).SelectDetailed(metric);

        Assert.Equal(expected, reading.Percent);
        Assert.Equal(name, reading.WindowName);
    }

    [Fact]
    public void An_unknown_reading_names_no_window()
    {
        Assert.Null(Snapshot().SelectDetailed(UsageMetric.HighestOfAll).WindowName);
        Assert.Null(Snapshot(sevenDay: 50).SelectDetailed(UsageMetric.FiveHour).WindowName);
        Assert.Null(UsageSnapshot.Unavailable("none", Now).SelectDetailed(UsageMetric.SevenDay).WindowName);
    }

    [Fact]
    public void The_detailed_reading_never_disagrees_with_the_gate()
    {
        // Select is what the scheduler compares against the threshold; if the two ever diverged the
        // dashboard would explain a decision that was not made.
        var snapshot = Snapshot(fiveHour: 12, sevenDay: 20, opus: 77, sonnet: 41);

        foreach (var metric in Enum.GetValues<UsageMetric>())
        {
            Assert.Equal(snapshot.Select(metric), snapshot.SelectDetailed(metric).Percent);
        }
    }

    [Fact]
    public void Earliest_reset_considers_only_windows_at_or_above_the_threshold()
    {
        var soon = Now.AddHours(1);
        var later = Now.AddDays(2);

        var snapshot = Snapshot(
            fiveHour: 40,
            sevenDay: 95,
            fiveHourReset: soon,
            sevenDayReset: later);

        Assert.Equal(later, UsageMetricSelector.EarliestResetAtOrAbove(snapshot, 90));
        Assert.Equal(soon, UsageMetricSelector.EarliestResetAtOrAbove(snapshot, 30));
        Assert.Null(UsageMetricSelector.EarliestResetAtOrAbove(snapshot, 99));
    }
}
