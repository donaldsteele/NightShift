using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NightShift.Core.Configuration;
using NightShift.Core.Io;

namespace NightShift.Core.Scheduling;

/// <summary>
/// Scheduler state that must survive a restart (plan.md §8, <c>state.json</c>).
/// </summary>
/// <remarks>
/// Settable properties for the same System.Text.Json source-generator reason documented on
/// <see cref="PilotSettings"/>: <c>init</c> properties become constructor parameters and absent
/// JSON fields silently become <c>default(T)</c>.
/// </remarks>
public sealed record PilotState
{
    /// <summary>When the next cycle is due. Null until the first tick is scheduled.</summary>
    public DateTimeOffset? NextRunAtUtc { get; set; }

    /// <summary>Session id of the last run, for <see cref="ResumeStrategy.Resume"/>.</summary>
    public string? LastSessionId { get; set; }

    /// <summary>Whether the pilot was paused when the app last closed.</summary>
    public bool IsPaused { get; set; }

    /// <summary>Id of the last completed run, so the dashboard can show it immediately on start.</summary>
    public string? LastRunId { get; set; }

    /// <summary>
    /// A session that stopped mid-task and should be continued when quota returns.
    /// </summary>
    /// <remarks>
    /// Persisted because a quota block routinely outlives the app: a five-hour window that ran out
    /// at midnight is not back until morning, and the machine may well be restarted in between.
    /// Losing this would silently downgrade "continue the half-finished item" to "start again".
    /// </remarks>
    public string? PendingResumeSessionId { get; set; }

    /// <summary>When the quota that stopped the last run comes back, if it reported one.</summary>
    public DateTimeOffset? QuotaResumesAtUtc { get; set; }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PilotState))]
public sealed partial class PilotStateJsonContext : JsonSerializerContext;

/// <summary>
/// Reads and writes <c>state.json</c>. Like the settings store, a corrupt file is recovered from
/// rather than fatal — losing the schedule must not stop the app starting.
/// </summary>
public sealed class PilotStateStore
{
    readonly AppPaths _paths;
    readonly ILogger<PilotStateStore> _logger;
    readonly SemaphoreSlim _gate = new(1, 1);

    public PilotStateStore(AppPaths paths, ILogger<PilotStateStore> logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<PilotState> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.StateFile))
        {
            return new PilotState();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_paths.StateFile, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize(json, PilotStateJsonContext.Default.PilotState) ?? new PilotState();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read {Path}; starting from a fresh schedule.", _paths.StateFile);
            return new PilotState();
        }
    }

    public async Task SaveAsync(PilotState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _paths.EnsureCreated();
            var json = JsonSerializer.Serialize(state, PilotStateJsonContext.Default.PilotState);
            await AtomicFile.WriteAllTextAsync(_paths.StateFile, json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The schedule is a convenience, not a correctness requirement: losing it costs one
            // interval, so never let it take down a run.
            _logger.LogWarning(ex, "Could not persist scheduler state.");
        }
        finally
        {
            _gate.Release();
        }
    }
}
