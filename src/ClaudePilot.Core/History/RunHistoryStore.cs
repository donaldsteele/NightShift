using System.Text;
using System.Text.Json;
using ClaudePilot.Core.Configuration;
using ClaudePilot.Core.Io;
using ClaudePilot.Core.Serialization;
using Microsoft.Extensions.Logging;

namespace ClaudePilot.Core.History;

/// <summary>
/// Append-only run history: one JSON object per line in <c>runs/index.jsonl</c>, with the full
/// transcript of each run beside it as <c>runs/&lt;runId&gt;.log</c> (plan.md §8).
/// A malformed line is skipped, never fatal — history is diagnostics, not state the app needs.
/// </summary>
public sealed class RunHistoryStore
{
    const int WriteRetryCount = 5;
    const int WriteRetryDelayMs = 25;

    readonly AppPaths _paths;
    readonly ILogger<RunHistoryStore> _logger;
    readonly SemaphoreSlim _gate = new(1, 1);

    public RunHistoryStore(AppPaths paths, ILogger<RunHistoryStore> logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Where the transcript for <paramref name="runId"/> lives.</summary>
    public string TranscriptPath(string runId) => _paths.RunTranscriptFile(runId);

    public async Task AppendAsync(RunRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.Id);

        var line = JsonSerializer.Serialize(record, ClaudePilotJsonLinesContext.Default.RunRecord);
        if (line.Contains('\n', StringComparison.Ordinal))
        {
            // WriteIndented would break the one-record-per-line contract.
            throw new InvalidOperationException("Serialized RunRecord must not contain newlines.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _paths.EnsureCreated();
            await AppendLineWithRetryAsync(_paths.RunIndexFile, line, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Every readable record, oldest first.</summary>
    public async Task<IReadOnlyList<RunRecord>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        var file = _paths.RunIndexFile;
        if (!File.Exists(file))
        {
            return [];
        }

        var records = new List<RunRecord>();
        var skipped = 0;

        await foreach (var line in ReadLinesAsync(file, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var record = JsonSerializer.Deserialize(line, ClaudePilotJsonLinesContext.Default.RunRecord);
                if (record is { Id.Length: > 0 })
                {
                    records.Add(record);
                }
                else
                {
                    skipped++;
                }
            }
            catch (JsonException)
            {
                skipped++;
            }
        }

        if (skipped > 0)
        {
            _logger.LogWarning("Skipped {Count} unreadable line(s) in {File}.", skipped, file);
        }

        return records;
    }

    /// <summary>
    /// Keeps the newest <paramref name="retainCount"/> records and deletes the transcripts of the
    /// ones dropped. Rewrites the index atomically so a crash cannot truncate it.
    /// </summary>
    public async Task<int> PruneAsync(int retainCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retainCount, 1);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = await ReadAllAsync(cancellationToken).ConfigureAwait(false);
            if (all.Count <= retainCount)
            {
                return 0;
            }

            var dropped = all.Take(all.Count - retainCount).ToList();
            var kept = all.Skip(all.Count - retainCount).ToList();

            var builder = new StringBuilder();
            foreach (var record in kept)
            {
                builder.Append(JsonSerializer.Serialize(record, ClaudePilotJsonLinesContext.Default.RunRecord));
                builder.Append('\n');
            }

            await AtomicFile.WriteAllTextAsync(_paths.RunIndexFile, builder.ToString(), cancellationToken)
                .ConfigureAwait(false);

            foreach (var record in dropped)
            {
                TryDeleteTranscript(record);
            }

            _logger.LogInformation("Pruned {Dropped} run(s); {Kept} retained.", dropped.Count, kept.Count);
            return dropped.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Appends streamed output to a run's transcript file.</summary>
    public async Task AppendTranscriptAsync(
        string runId,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(text);

        _paths.EnsureCreated();
        await using var stream = new FileStream(
            TranscriptPath(runId),
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        await using var writer = new StreamWriter(stream, Encoding.UTF8);
        await writer.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ReadTranscriptAsync(string runId, CancellationToken cancellationToken = default)
    {
        var path = TranscriptPath(runId);
        return File.Exists(path)
            ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
            : string.Empty;
    }

    async Task AppendLineWithRetryAsync(string file, string line, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var stream = new FileStream(
                    file,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 4096,
                    useAsync: true);
                await using var writer = new StreamWriter(stream, Encoding.UTF8);
                await writer.WriteAsync((line + "\n").AsMemory(), cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (IOException) when (attempt < WriteRetryCount)
            {
                // Another process (a second ClaudePilot instance, a text editor) holds the file.
                await Task.Delay(WriteRetryDelayMs * attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    static async IAsyncEnumerable<string> ReadLinesAsync(
        string file,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            file,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4096,
            useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            yield return line;
        }
    }

    void TryDeleteTranscript(RunRecord record)
    {
        var path = record.TranscriptPath ?? TranscriptPath(record.Id);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete transcript {Path} while pruning.", path);
        }
    }
}
