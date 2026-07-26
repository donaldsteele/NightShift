using NightShift.Core.Configuration;
using NightShift.Core.History;
using NightShift.Core.Usage;

namespace NightShift.Core.Scheduling;

/// <summary>What a tick decided to do.</summary>
public enum CycleAction
{
    Run,
    Skip,
}

/// <summary>
/// The outcome of one scheduler tick (plan.md §6).
/// </summary>
/// <param name="Action">Run or skip.</param>
/// <param name="Reason">Why it was skipped. <see cref="SkipReason.None"/> when running.</param>
/// <param name="Detail">Human-readable explanation for the log, the history row and the status pill.</param>
/// <param name="Usage">The snapshot the decision was made from, if it got that far.</param>
/// <param name="MetricPercent">The selected metric's value, when there was one.</param>
/// <param name="ResumeAt">
/// When the blocking window rolls over, if known. The scheduler brings the next check forward to
/// this rather than waiting out a full interval (plan.md §6, step 5).
/// </param>
public sealed record CycleDecision(
    CycleAction Action,
    SkipReason Reason,
    string Detail,
    UsageSnapshot? Usage = null,
    double? MetricPercent = null,
    DateTimeOffset? ResumeAt = null)
{
    public bool ShouldRun => Action == CycleAction.Run;

    public static CycleDecision Run(UsageSnapshot? usage, double? metric, string detail) =>
        new(CycleAction.Run, SkipReason.None, detail, usage, metric);

    public static CycleDecision Skip(
        SkipReason reason,
        string detail,
        UsageSnapshot? usage = null,
        double? metric = null,
        DateTimeOffset? resumeAt = null) =>
        new(CycleAction.Skip, reason, detail, usage, metric, resumeAt);
}

/// <summary>
/// The run/skip rule from plan.md §6, kept apart from the timer so it can be tested exhaustively.
/// </summary>
/// <remarks>
/// The ordering of the checks is the specification, not an implementation detail: usage is fetched
/// only after the cheap local gates pass, so a disabled or already-running pilot never touches the
/// network.
/// </remarks>
public static class CycleDecisionMaker
{
    /// <summary>
    /// Applies the threshold rule to an already-fetched snapshot.
    /// </summary>
    /// <param name="settings">Threshold, metric and unavailable-behaviour come from here.</param>
    /// <param name="usage">What the usage providers reported.</param>
    public static CycleDecision FromUsage(PilotSettings settings, UsageSnapshot usage)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(usage);

        var metric = usage.Select(settings.UsageMetric);

        if (metric is null)
        {
            // Either every provider failed, or the selected window was absent. Both mean "we do not
            // know the quota", and guessing is how an unattended pilot burns a weekly cap.
            return settings.OnUsageUnavailable == UsageUnavailableBehavior.Run
                ? CycleDecision.Run(usage, null, "Usage unavailable, but the pilot is configured to run anyway.")
                : CycleDecision.Skip(
                    SkipReason.UsageUnavailable,
                    $"Usage unavailable ({usage.UnavailableReason ?? "no figure for " + settings.UsageMetric}); skipping.",
                    usage);
        }

        if (metric >= settings.ThresholdPercent)
        {
            var resumeAt = UsageMetricSelector.EarliestResetAtOrAbove(usage, settings.ThresholdPercent);
            var approximate = usage.IsApproximate ? " (approximate)" : string.Empty;

            return CycleDecision.Skip(
                SkipReason.OverThreshold,
                $"{settings.UsageMetric} at {metric:0.##}%{approximate} is at or above the {settings.ThresholdPercent:0.##}% threshold.",
                usage,
                metric,
                resumeAt);
        }

        return CycleDecision.Run(
            usage,
            metric,
            $"{settings.UsageMetric} at {metric:0.##}% is below the {settings.ThresholdPercent:0.##}% threshold.");
    }
}
