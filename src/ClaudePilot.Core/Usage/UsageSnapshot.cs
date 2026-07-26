namespace ClaudePilot.Core.Usage;

/// <summary>One rate-limit window as reported by a provider (plan.md §4.1).</summary>
/// <param name="UtilizationPercent">0–100. A missing window is recorded as 0, never as null.</param>
/// <param name="ResetsAt">When the window rolls over, if the provider said.</param>
public sealed record UsageWindow(double UtilizationPercent, DateTimeOffset? ResetsAt);

/// <summary>
/// What a usage provider knows at one moment (plan.md §4.3). Every window may be absent: the OAuth
/// endpoint returns `null` for windows that do not apply to the account.
/// </summary>
public sealed record UsageSnapshot(
    UsageWindow? FiveHour,
    UsageWindow? SevenDay,
    UsageWindow? SevenDayOpus,
    UsageWindow? SevenDaySonnet,
    UsageSource Source,
    bool IsApproximate,
    DateTimeOffset RetrievedAt)
{
    /// <summary>Why no figure could be obtained. Null whenever <see cref="IsAvailable"/> is true.</summary>
    public string? UnavailableReason { get; set; }

    /// <summary>False when every provider failed — the scheduler then applies `OnUsageUnavailable`.</summary>
    public bool IsAvailable => Source != UsageSource.None;

    public static UsageSnapshot Unavailable(string reason, DateTimeOffset retrievedAt) =>
        new(null, null, null, null, UsageSource.None, IsApproximate: false, retrievedAt)
        {
            UnavailableReason = reason,
        };
}
