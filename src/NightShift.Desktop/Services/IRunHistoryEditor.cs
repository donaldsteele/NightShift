using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NightShift.Core.Configuration;
using NightShift.Core.History;
using NightShift.Core.Io;
using NightShift.Core.Serialization;

namespace NightShift.Desktop.Services;

/// <summary>
/// Deleting a single run — the right-click <c>Delete</c> action of plan.md §9.3.
/// </summary>
/// <remarks>
/// <see cref="RunHistoryStore"/> is append-only by design and its only removal path,
/// <see cref="RunHistoryStore.PruneAsync"/>, drops the <em>oldest</em> N. Deleting one arbitrary row
/// is a UI affordance, not a scheduler concern, so it lives in the app layer rather than widening
/// Core's API.
/// </remarks>
public interface IRunHistoryEditor
{
    /// <summary>
    /// Removes the record from <c>runs/index.jsonl</c> and deletes its transcript. Returns false
    /// when no such record exists; never throws for an I/O failure.
    /// </summary>
    Task<bool> DeleteAsync(string runId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Rewrites <c>runs/index.jsonl</c> without one record, atomically, using the same serializer
/// context the store writes with so a round-trip cannot change the shape of the surviving lines.
/// </summary>
public sealed class RunHistoryEditor : IRunHistoryEditor
{
    readonly AppPaths _paths;
    readonly RunHistoryStore _history;
    readonly ILogger<RunHistoryEditor> _logger;
    readonly SemaphoreSlim _gate = new(1, 1);

    public RunHistoryEditor(AppPaths paths, RunHistoryStore history, ILogger<RunHistoryEditor> logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> DeleteAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = await _history.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            var target = all.FirstOrDefault(record => record.Id == runId);
            if (target is null)
            {
                return false;
            }

            var builder = new StringBuilder();
            foreach (var record in all)
            {
                if (record.Id == runId)
                {
                    continue;
                }

                builder.Append(JsonSerializer.Serialize(record, NightShiftJsonLinesContext.Default.RunRecord));
                builder.Append('\n');
            }

            try
            {
                await AtomicFile
                    .WriteAllTextAsync(_paths.RunIndexFile, builder.ToString(), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not rewrite the run index while deleting {RunId}.", runId);
                return false;
            }

            TryDeleteTranscript(target);
            _logger.LogInformation("Deleted run {RunId} from the history.", runId);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    void TryDeleteTranscript(RunRecord record)
    {
        var path = record.TranscriptPath ?? _history.TranscriptPath(record.Id);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete transcript {Path}.", path);
        }
    }
}
