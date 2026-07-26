namespace ClaudePilot.Core.Usage;

/// <summary>
/// One source of usage figures (plan.md §4). <c>CompositeUsageProvider</c> asks each in turn, so an
/// implementation must be a *reporter*, never a thrower: every expected failure — no credentials, a
/// dead endpoint, a non-zero exit code, a rate limit — comes back as
/// <see cref="UsageSnapshot.Unavailable(string, DateTimeOffset)"/> with a reason the UI can show.
/// Only genuinely exceptional conditions (a cancelled <see cref="CancellationToken"/>, a bug) throw.
/// </summary>
public interface IUsageProvider
{
    /// <summary>Which source this provider stamps on the snapshots it produces.</summary>
    UsageSource Source { get; }

    /// <summary>
    /// The current usage figures, or an unavailable snapshot explaining why there are none.
    /// </summary>
    Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default);
}
