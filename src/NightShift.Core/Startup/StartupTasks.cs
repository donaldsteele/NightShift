using NightShift.Core.Configuration;
using NightShift.Core.History;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NightShift.Core.Startup;

/// <summary>
/// Loads settings and prunes run history once at startup (plan.md §8, §9.3 "prune on startup").
/// Nothing here may throw: a failure to read persisted state must not stop the app from starting.
/// </summary>
public sealed class StartupTasks : IHostedService
{
    readonly ISettingsStore _settings;
    readonly RunHistoryStore _history;
    readonly ILogger<StartupTasks> _logger;

    public StartupTasks(ISettingsStore settings, RunHistoryStore history, ILogger<StartupTasks> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Settings loaded: interval {Interval}m, threshold {Threshold}% on {Metric}, launch mode {LaunchMode}.",
            settings.IntervalMinutes,
            settings.ThresholdPercent,
            settings.UsageMetric,
            settings.LaunchMode);

        try
        {
            await _history.PruneAsync(settings.HistoryRetentionCount, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not prune run history at startup.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
