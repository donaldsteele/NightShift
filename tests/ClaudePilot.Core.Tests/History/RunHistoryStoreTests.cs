using ClaudePilot.Core.Configuration;
using ClaudePilot.Core.History;
using ClaudePilot.Core.Usage;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudePilot.Core.Tests.History;

public sealed class RunHistoryStoreTests : IDisposable
{
    readonly TempDirectory _temp = new();
    readonly AppPaths _paths;
    readonly RunHistoryStore _store;

    public RunHistoryStoreTests()
    {
        _paths = new AppPaths(_temp.Path);
        _store = new RunHistoryStore(_paths, NullLogger<RunHistoryStore>.Instance);
    }

    static RunRecord Record(string id, RunOutcome outcome = RunOutcome.Success) => new()
    {
        Id = id,
        StartedAt = new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero),
        EndedAt = new DateTimeOffset(2026, 7, 26, 12, 5, 0, TimeSpan.Zero),
        Outcome = outcome,
    };

    [Fact]
    public async Task Reading_an_empty_history_returns_nothing()
    {
        Assert.Empty(await _store.ReadAllAsync());
    }

    [Fact]
    public async Task Records_round_trip_with_every_field()
    {
        var record = new RunRecord
        {
            Id = "20260726-120000-abc123",
            StartedAt = new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.FromHours(-4)),
            EndedAt = new DateTimeOffset(2026, 7, 26, 12, 42, 0, TimeSpan.FromHours(-4)),
            Outcome = RunOutcome.TimedOut,
            SkipReason = SkipReason.None,
            SkipDetail = null,
            UsageAtStart = new UsageSnapshot(
                new UsageWindow(33, new DateTimeOffset(2026, 7, 26, 17, 0, 0, TimeSpan.Zero)),
                new UsageWindow(13, null),
                null,
                new UsageWindow(1, null),
                UsageSource.OAuth,
                IsApproximate: false,
                RetrievedAt: new DateTimeOffset(2026, 7, 26, 11, 59, 0, TimeSpan.Zero)),
            SessionId = "sess-1",
            CostUsd = 1.25,
            ExitCode = 143,
            PermissionModeUsed = PermissionMode.AcceptEdits,
            TranscriptPath = _store.TranscriptPath("20260726-120000-abc123"),
            Summary = "did the thing",
        };

        await _store.AppendAsync(record);
        var all = await _store.ReadAllAsync();

        var read = Assert.Single(all);
        Assert.Equal(record, read);
        Assert.Equal(TimeSpan.FromMinutes(42), read.Duration);
    }

    [Fact]
    public async Task Index_holds_exactly_one_line_per_record()
    {
        await _store.AppendAsync(Record("a"));
        await _store.AppendAsync(Record("b"));

        var lines = await File.ReadAllLinesAsync(_paths.RunIndexFile);

        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.StartsWith("{", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unreadable_lines_are_skipped_not_fatal()
    {
        await _store.AppendAsync(Record("good-1"));
        await File.AppendAllTextAsync(_paths.RunIndexFile, "{ this is not json\n\n");
        await _store.AppendAsync(Record("good-2"));

        var all = await _store.ReadAllAsync();

        Assert.Equal(["good-1", "good-2"], all.Select(r => r.Id));
    }

    [Fact]
    public async Task Summary_is_truncated()
    {
        var record = Record("long").WithSummary(new string('x', RunRecord.MaxSummaryLength + 250));

        await _store.AppendAsync(record);

        var read = Assert.Single(await _store.ReadAllAsync());
        Assert.Equal(RunRecord.MaxSummaryLength, read.Summary!.Length);
    }

    [Fact]
    public async Task Pruning_keeps_the_newest_records_and_deletes_their_transcripts()
    {
        for (var i = 0; i < 5; i++)
        {
            var id = $"run-{i}";
            await _store.AppendTranscriptAsync(id, $"transcript {i}");
            await _store.AppendAsync(Record(id) with { TranscriptPath = _store.TranscriptPath(id) });
        }

        var dropped = await _store.PruneAsync(retainCount: 2);

        Assert.Equal(3, dropped);
        Assert.Equal(["run-3", "run-4"], (await _store.ReadAllAsync()).Select(r => r.Id));
        Assert.False(File.Exists(_store.TranscriptPath("run-0")));
        Assert.False(File.Exists(_store.TranscriptPath("run-2")));
        Assert.True(File.Exists(_store.TranscriptPath("run-3")));
        Assert.Equal("transcript 4", await _store.ReadTranscriptAsync("run-4"));
    }

    [Fact]
    public async Task Pruning_below_the_retention_count_is_a_no_op()
    {
        await _store.AppendAsync(Record("only"));

        Assert.Equal(0, await _store.PruneAsync(retainCount: 200));
        Assert.Single(await _store.ReadAllAsync());
    }

    [Fact]
    public async Task Concurrent_appends_do_not_interleave()
    {
        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => _store.AppendAsync(Record($"run-{i:00}"))));

        var all = await _store.ReadAllAsync();

        Assert.Equal(50, all.Count);
        Assert.Equal(50, all.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public async Task Transcript_appends_accumulate()
    {
        await _store.AppendTranscriptAsync("run-x", "first\n");
        await _store.AppendTranscriptAsync("run-x", "second\n");

        Assert.Equal("first\nsecond\n", await _store.ReadTranscriptAsync("run-x"));
    }

    [Fact]
    public async Task Start_produces_sortable_unique_ids()
    {
        var start = new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
        var first = RunRecord.Start(start);
        var second = RunRecord.Start(start.AddSeconds(1));

        Assert.StartsWith("20260726-120000-", first.Id, StringComparison.Ordinal);
        Assert.NotEqual(first.Id, second.Id);
        Assert.True(string.CompareOrdinal(first.Id, second.Id) < 0);
        await Task.CompletedTask;
    }

    public void Dispose() => _temp.Dispose();
}
