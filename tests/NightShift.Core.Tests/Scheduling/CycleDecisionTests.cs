using NightShift.Core.Configuration;
using NightShift.Core.History;
using NightShift.Core.Scheduling;
using NightShift.Core.Usage;

namespace NightShift.Core.Tests.Scheduling;

public sealed class CycleDecisionTests
{
    static readonly DateTimeOffset Now = new(2026, 7, 26, 22, 0, 0, TimeSpan.Zero);

    static UsageSnapshot Usage(
        double? fiveHour = null,
        double? sevenDay = null,
        bool approximate = false,
        DateTimeOffset? fiveHourReset = null,
        DateTimeOffset? sevenDayReset = null) =>
        new(
            fiveHour is { } f ? new UsageWindow(f, fiveHourReset) : null,
            sevenDay is { } s ? new UsageWindow(s, sevenDayReset) : null,
            null,
            null,
            approximate ? UsageSource.Ccusage : UsageSource.OAuth,
            approximate,
            Now);

    static PilotSettings Settings(Action<PilotSettings>? mutate = null)
    {
        var settings = new PilotSettings();
        mutate?.Invoke(settings);
        return settings;
    }

    [Theory]
    [InlineData(89, true)]
    [InlineData(89.99, true)]
    [InlineData(90, false)]
    [InlineData(91, false)]
    public void The_threshold_is_exclusive_at_the_boundary(double utilization, bool shouldRun)
    {
        // plan.md §12: at 89 it runs, at 90 it skips, at 91 it skips.
        var decision = CycleDecisionMaker.FromUsage(
            Settings(s => s.UsageMetric = UsageMetric.FiveHour),
            Usage(fiveHour: utilization));

        Assert.Equal(shouldRun, decision.ShouldRun);
        if (!shouldRun)
        {
            Assert.Equal(SkipReason.OverThreshold, decision.Reason);
        }
    }

    [Fact]
    public void HighestOfAll_blocks_when_only_the_weekly_window_is_hot()
    {
        // plan.md §12: the default metric must not start a run that immediately burns the weekly cap.
        var decision = CycleDecisionMaker.FromUsage(Settings(), Usage(fiveHour: 5, sevenDay: 94));

        Assert.False(decision.ShouldRun);
        Assert.Equal(SkipReason.OverThreshold, decision.Reason);
        Assert.Equal(94, decision.MetricPercent);
    }

    [Fact]
    public void A_hot_weekly_window_does_not_block_when_the_metric_is_the_session_window()
    {
        var decision = CycleDecisionMaker.FromUsage(
            Settings(s => s.UsageMetric = UsageMetric.FiveHour),
            Usage(fiveHour: 5, sevenDay: 94));

        Assert.True(decision.ShouldRun);
    }

    [Fact]
    public void Unavailable_usage_skips_by_default()
    {
        var decision = CycleDecisionMaker.FromUsage(
            Settings(),
            UsageSnapshot.Unavailable("everything is broken", Now));

        Assert.False(decision.ShouldRun);
        Assert.Equal(SkipReason.UsageUnavailable, decision.Reason);
        Assert.Contains("everything is broken", decision.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Unavailable_usage_runs_only_when_explicitly_configured_to()
    {
        var decision = CycleDecisionMaker.FromUsage(
            Settings(s => s.OnUsageUnavailable = UsageUnavailableBehavior.Run),
            UsageSnapshot.Unavailable("everything is broken", Now));

        Assert.True(decision.ShouldRun);
    }

    [Fact]
    public void An_absent_window_counts_as_unavailable_not_as_zero()
    {
        // A fabricated 0 would read as "plenty of quota left" and start a run.
        var decision = CycleDecisionMaker.FromUsage(
            Settings(s => s.UsageMetric = UsageMetric.SevenDay),
            Usage(fiveHour: 10));

        Assert.False(decision.ShouldRun);
        Assert.Equal(SkipReason.UsageUnavailable, decision.Reason);
    }

    [Fact]
    public void A_blocked_cycle_reports_when_the_window_resets_so_the_scheduler_can_resume_early()
    {
        var fiveHourReset = Now.AddHours(2);
        var sevenDayReset = Now.AddDays(3);

        var decision = CycleDecisionMaker.FromUsage(
            Settings(),
            Usage(fiveHour: 95, sevenDay: 96, fiveHourReset: fiveHourReset, sevenDayReset: sevenDayReset));

        Assert.False(decision.ShouldRun);
        Assert.Equal(fiveHourReset, decision.ResumeAt);
    }

    [Fact]
    public void Only_windows_over_the_threshold_influence_the_resume_time()
    {
        var fiveHourReset = Now.AddHours(2);
        var sevenDayReset = Now.AddDays(3);

        var decision = CycleDecisionMaker.FromUsage(
            Settings(),
            Usage(fiveHour: 10, sevenDay: 96, fiveHourReset: fiveHourReset, sevenDayReset: sevenDayReset));

        Assert.Equal(sevenDayReset, decision.ResumeAt);
    }

    [Fact]
    public void An_approximate_figure_is_called_out_in_the_detail()
    {
        var decision = CycleDecisionMaker.FromUsage(Settings(), Usage(fiveHour: 95, approximate: true));

        Assert.Contains("approximate", decision.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_custom_threshold_is_honoured()
    {
        var settings = Settings(s =>
        {
            s.ThresholdPercent = 60;
            s.UsageMetric = UsageMetric.FiveHour;
        });

        Assert.True(CycleDecisionMaker.FromUsage(settings, Usage(fiveHour: 59)).ShouldRun);
        Assert.False(CycleDecisionMaker.FromUsage(settings, Usage(fiveHour: 61)).ShouldRun);
    }
}
