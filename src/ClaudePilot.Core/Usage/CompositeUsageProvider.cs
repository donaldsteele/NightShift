using ClaudePilot.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace ClaudePilot.Core.Usage;

/// <summary>What the composite last saw from one underlying provider (plan.md §4.3).</summary>
/// <param name="Source">Which provider.</param>
/// <param name="IsHealthy">Whether its most recent attempt produced usable figures.</param>
/// <param name="LastReason">Why it failed, when it did.</param>
/// <param name="LastAttemptAt">When it was last asked.</param>
public sealed record UsageProviderHealth(
    UsageSource Source,
    bool IsHealthy,
    string? LastReason,
    DateTimeOffset LastAttemptAt);

/// <summary>
/// Asks each configured provider in preference order and returns the first usable answer
/// (plan.md §4.3). Returns <see cref="UsageSnapshot.Unavailable(string, DateTimeOffset)"/> only when
/// every provider failed — the scheduler then applies <c>OnUsageUnavailable</c>, which defaults to
/// skipping the cycle.
/// </summary>
public sealed class CompositeUsageProvider : IUsageProvider
{
    readonly IReadOnlyList<IUsageProvider> _providers;
    readonly ISettingsStore _settings;
    readonly ILogger<CompositeUsageProvider> _logger;
    readonly TimeProvider _timeProvider;
    readonly Dictionary<UsageSource, UsageProviderHealth> _health = [];
    readonly Lock _healthLock = new();

    public CompositeUsageProvider(
        IEnumerable<IUsageProvider> providers,
        ISettingsStore settings,
        ILogger<CompositeUsageProvider> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(providers);

        _providers = providers.Where(p => p is not CompositeUsageProvider).ToList();
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// <see cref="UsageSource.None"/>: the composite stamps nothing of its own — every snapshot it
    /// returns carries the source of the provider that produced it.
    /// </summary>
    public UsageSource Source => UsageSource.None;

    /// <summary>Last-attempt state per provider, for the dashboard's provider health display.</summary>
    public IReadOnlyList<UsageProviderHealth> Health
    {
        get
        {
            lock (_healthLock)
            {
                return [.. _health.Values];
            }
        }
    }

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        var order = _settings.Current.UsageProviderOrder.ToSequence();
        var reasons = new List<string>();

        foreach (var source in order)
        {
            var provider = _providers.FirstOrDefault(p => p.Source == source);
            if (provider is null)
            {
                continue;
            }

            var now = _timeProvider.GetUtcNow();
            UsageSnapshot snapshot;
            try
            {
                snapshot = await provider.GetUsageAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A provider is contractually a reporter, not a thrower — but a bug in one must not
                // take down the cycle, because "no usage" already has a safe defined behaviour.
                _logger.LogError(ex, "Usage provider {Source} threw; treating it as unavailable.", source);
                RecordHealth(source, isHealthy: false, ex.Message, now);
                reasons.Add($"{source}: {ex.Message}");
                continue;
            }

            if (snapshot.IsAvailable)
            {
                RecordHealth(source, isHealthy: true, null, now);
                return snapshot;
            }

            var reason = snapshot.UnavailableReason ?? "no reason given";
            _logger.LogInformation("Usage provider {Source} unavailable: {Reason}", source, reason);
            RecordHealth(source, isHealthy: false, reason, now);
            reasons.Add($"{source}: {reason}");
        }

        var summary = reasons.Count > 0
            ? string.Join("; ", reasons)
            : "no usage providers are configured";

        _logger.LogWarning("No usage provider could report a figure. {Reasons}", summary);
        return UsageSnapshot.Unavailable(summary, _timeProvider.GetUtcNow());
    }

    void RecordHealth(UsageSource source, bool isHealthy, string? reason, DateTimeOffset at)
    {
        lock (_healthLock)
        {
            _health[source] = new UsageProviderHealth(source, isHealthy, reason, at);
        }
    }
}
