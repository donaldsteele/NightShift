using CommunityToolkit.Mvvm.ComponentModel;
using NightShift.Core.Usage;

namespace NightShift.Desktop.ViewModels;

/// <summary>
/// One of the two usage gauges on the dashboard — "Session (5h)" and "Weekly (7d)" (plan.md §9.1).
/// </summary>
/// <remarks>
/// <para>
/// The five observable properties are the whole binding contract with the <c>UsageGauge</c> control:
/// <see cref="Percent"/>, <see cref="Caption"/>, <see cref="SubCaption"/>,
/// <see cref="IsApproximate"/> and <see cref="IsUnavailable"/>. Nothing else is meant to be bound.
/// </para>
/// <para>
/// <see cref="IsUnavailable"/> is <b>not</b> "0%". A provider that reports nothing for a window
/// means the quota is unknown, and plan.md §4.3 is emphatic that unknown must never be rendered as
/// "plenty left" — so the gauge starts unavailable and only leaves that state when a real figure
/// arrives.
/// </para>
/// </remarks>
public sealed partial class UsageGaugeViewModel : ViewModelBase
{
    public UsageGaugeViewModel(string caption)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caption);
        Caption = caption;
    }

    /// <summary>Utilization, 0–100. Meaningless while <see cref="IsUnavailable"/> is true.</summary>
    [ObservableProperty]
    public partial double Percent { get; set; }

    /// <summary>The window's name, e.g. <c>Session (5h)</c>.</summary>
    [ObservableProperty]
    public partial string Caption { get; set; }

    /// <summary>The line underneath: <c>resets in 2h 14m</c>, or why there is no figure.</summary>
    [ObservableProperty]
    public partial string SubCaption { get; set; } = string.Empty;

    /// <summary>
    /// True when the figure came from <c>ccusage</c> rather than the OAuth endpoint — token-derived,
    /// so the gauge shows an "approximate" chip (plan.md §4.2, §9.1).
    /// </summary>
    [ObservableProperty]
    public partial bool IsApproximate { get; set; }

    /// <summary>True when no provider could report this window. The gauge renders empty, not zero.</summary>
    [ObservableProperty]
    public partial bool IsUnavailable { get; set; } = true;

    /// <summary>
    /// When this window rolls over, if the provider said. Kept so
    /// <see cref="RefreshSubCaption"/> can re-render the countdown on a timer without another
    /// usage lookup — reset times are absolute, so a snapshot stays usable long after it was taken.
    /// </summary>
    public DateTimeOffset? ResetsAt { get; private set; }

    /// <summary>Applies one window of a snapshot to the gauge.</summary>
    /// <param name="window">The window, or null when the provider did not report it.</param>
    /// <param name="isApproximate">The snapshot's <see cref="UsageSnapshot.IsApproximate"/>.</param>
    /// <param name="unavailableReason">
    /// Shown as the sub-caption when there is no figure. Falls back to a generic line.
    /// </param>
    /// <param name="now">Reference time for the countdown.</param>
    public void Update(
        UsageWindow? window,
        bool isApproximate,
        string? unavailableReason,
        DateTimeOffset now)
    {
        if (window is null)
        {
            ResetsAt = null;
            IsUnavailable = true;
            IsApproximate = false;
            Percent = 0;
            SubCaption = string.IsNullOrWhiteSpace(unavailableReason)
                ? "no figure reported"
                : unavailableReason;
            return;
        }

        ResetsAt = window.ResetsAt;
        IsUnavailable = false;
        IsApproximate = isApproximate;
        Percent = Math.Clamp(window.UtilizationPercent, 0, 100);
        RefreshSubCaption(now);
    }

    /// <summary>Marks the gauge as having no figure at all, with a reason for the sub-caption.</summary>
    public void SetUnavailable(string? reason)
    {
        ResetsAt = null;
        IsUnavailable = true;
        IsApproximate = false;
        Percent = 0;
        SubCaption = string.IsNullOrWhiteSpace(reason) ? "usage unavailable" : reason;
    }

    /// <summary>
    /// Re-renders <c>resets in 2h 14m</c> against <paramref name="now"/>. Called on the dashboard's
    /// tick so the countdown stays live between usage lookups.
    /// </summary>
    public void RefreshSubCaption(DateTimeOffset now)
    {
        if (IsUnavailable)
        {
            return;
        }

        // The control ships the wording for this line, so the gauge and everything else that counts
        // down stay identical rather than nearly identical.
        SubCaption = Controls.RelativeTimeFormatter.FormatReset(ResetsAt, now)
            ?? "reset time not reported";
    }
}
