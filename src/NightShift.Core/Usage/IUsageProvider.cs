namespace NightShift.Core.Usage;

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
    /// <param name="forceRefresh">
    /// Ignore any cached response and go to the source. A provider without a cache ignores this.
    /// <para>
    /// "Force" means force past the <em>cache</em>, never past a rate limit: an active 429 backoff
    /// or a latched 401 still short-circuits, because hammering an endpoint that just said no is the
    /// failure this app is built to avoid (plan.md §4.1).
    /// </para>
    /// </param>
    Task<UsageSnapshot> GetUsageAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);
}
