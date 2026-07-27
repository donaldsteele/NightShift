using NightShift.Core.History;
using NightShift.Core.Usage;

namespace NightShift.Desktop.Tests;

/// <summary>plan.md §9.3: the run grid, its outcome chips and the transcript pane.</summary>
public class HistoryViewModelTests
{
    static async Task SeedAsync(ViewModelHarness harness)
    {
        var start = harness.Time.GetUtcNow();

        await harness.History.AppendAsync(Record(start, RunOutcome.Success, "sess-1", 0.42));
        await harness.History.AppendAsync(Record(start.AddMinutes(60), RunOutcome.Skipped, null, null));
        await harness.History.AppendAsync(Record(start.AddMinutes(120), RunOutcome.RateLimited, "sess-2", 1.10));
        await harness.History.AppendAsync(Record(start.AddMinutes(180), RunOutcome.Failed, "sess-3", null));
        await harness.History.AppendAsync(Record(start.AddMinutes(240), RunOutcome.Success, "sess-4", 0.07));
    }

    static RunRecord Record(DateTimeOffset startedAt, RunOutcome outcome, string? sessionId, double? cost) =>
        RunRecord.Start(startedAt) with
        {
            StartedAt = startedAt,
            EndedAt = startedAt.AddMinutes(9),
            Outcome = outcome,
            SessionId = sessionId,
            CostUsd = cost,
            Summary = $"{outcome} run",
            UsageAtStart = new UsageSnapshot(
                new UsageWindow(72, startedAt.AddHours(3)),
                new UsageWindow(41, startedAt.AddDays(4)),
                null,
                null,
                UsageSource.OAuth,
                IsApproximate: false,
                startedAt),
        };

    [Fact]
    public async Task LoadsEveryRunNewestFirst()
    {
        using var harness = new ViewModelHarness();
        await SeedAsync(harness);

        using var history = harness.CreateHistory();
        await history.InitializeAsync();

        Assert.Equal(5, history.TotalRunCount);
        Assert.Equal(5, history.Runs.Count);
        Assert.Equal(RunOutcome.Success, history.Runs[0].Outcome);
        Assert.Equal("sess-4", history.Runs[0].SessionId);
        Assert.True(history.Runs[0].StartedAt > history.Runs[^1].StartedAt);
        Assert.False(history.IsEmpty);
    }

    [Fact]
    public async Task WithNoChipSelectedEverythingIsShown()
    {
        using var harness = new ViewModelHarness();
        await SeedAsync(harness);

        using var history = harness.CreateHistory();
        await history.InitializeAsync();

        Assert.False(history.HasActiveFilters);
        Assert.Equal(5, history.Runs.Count);
    }

    [Fact]
    public async Task RateLimitedIsOneOfTheOutcomeChips()
    {
        using var harness = new ViewModelHarness();
        await SeedAsync(harness);

        using var history = harness.CreateHistory();
        await history.InitializeAsync();

        var chip = Assert.Single(history.OutcomeFilters, filter => filter.Outcome == RunOutcome.RateLimited);
        Assert.Equal("Rate limited", chip.Label);
        Assert.Equal(1, chip.Count);
    }

    [Fact]
    public async Task SelectingAChipNarrowsTheGridToThatOutcome()
    {
        using var harness = new ViewModelHarness();
        await SeedAsync(harness);

        using var history = harness.CreateHistory();
        await history.InitializeAsync();

        history.OutcomeFilters.Single(f => f.Outcome == RunOutcome.RateLimited).IsSelected = true;

        Assert.True(history.HasActiveFilters);
        var only = Assert.Single(history.Runs);
        Assert.Equal(RunOutcome.RateLimited, only.Outcome);
        Assert.True(only.IsRateLimited);
    }

    [Fact]
    public async Task SelectingSeveralChipsIsAUnion()
    {
        using var harness = new ViewModelHarness();
        await SeedAsync(harness);

        using var history = harness.CreateHistory();
        await history.InitializeAsync();

        history.OutcomeFilters.Single(f => f.Outcome == RunOutcome.RateLimited).IsSelected = true;
        history.OutcomeFilters.Single(f => f.Outcome == RunOutcome.Failed).IsSelected = true;

        Assert.Equal(2, history.ActiveFilterCount);
        Assert.Equal(2, history.Runs.Count);
        Assert.All(history.Runs, run =>
            Assert.True(run.Outcome is RunOutcome.RateLimited or RunOutcome.Failed));
    }

    [Fact]
    public async Task ClearingTheChipsShowsEverythingAgain()
    {
        using var harness = new ViewModelHarness();
        await SeedAsync(harness);

        using var history = harness.CreateHistory();
        await history.InitializeAsync();

        history.OutcomeFilters.Single(f => f.Outcome == RunOutcome.Skipped).IsSelected = true;
        Assert.Single(history.Runs);

        history.ClearOutcomeFiltersCommand.Execute(null);

        Assert.False(history.HasActiveFilters);
        Assert.Equal(5, history.Runs.Count);
    }

    [Fact]
    public async Task AFilterThatMatchesNothingProducesTheEmptyState()
    {
        using var harness = new ViewModelHarness();
        await SeedAsync(harness);

        using var history = harness.CreateHistory();
        await history.InitializeAsync();

        history.OutcomeFilters.Single(f => f.Outcome == RunOutcome.TimedOut).IsSelected = true;

        Assert.Empty(history.Runs);
        Assert.True(history.IsEmpty);
    }

    [Fact]
    public async Task SelectingARunLoadsItsTranscriptAndSearchFindsMatches()
    {
        using var harness = new ViewModelHarness();
        await SeedAsync(harness);

        using var history = harness.CreateHistory();
        await history.InitializeAsync();

        var run = history.Runs[0];
        await harness.History.AppendTranscriptAsync(run.Id, "alpha\nbeta\nalpha\ngamma\n");

        history.SelectedRun = run;
        Assert.True(SpinWait.SpinUntil(() => history.HasTranscript, 5000));

        history.TranscriptSearchText = "ALPHA";

        Assert.Equal(2, history.TranscriptMatchCount);
        Assert.Equal(0, history.SelectedMatchIndex);
        Assert.Equal("1 of 2", history.TranscriptMatchPositionText);

        history.NextMatchCommand.Execute(null);
        Assert.Equal(1, history.SelectedMatchIndex);
        Assert.Equal(11, history.SelectedMatchOffset);

        history.NextMatchCommand.Execute(null);
        Assert.Equal(0, history.SelectedMatchIndex);

        history.PreviousMatchCommand.Execute(null);
        Assert.Equal(1, history.SelectedMatchIndex);
    }

    [Fact]
    public async Task CopySessionIdIsOnlyOfferedForRunsThatHaveOne()
    {
        using var harness = new ViewModelHarness();
        await SeedAsync(harness);

        using var history = harness.CreateHistory();
        await history.InitializeAsync();

        history.SelectedRun = history.Runs.First(run => run.HasSessionId);
        Assert.True(history.CopySessionIdCommand.CanExecute(null));

        await history.CopySessionIdCommand.ExecuteAsync(null);
        Assert.Equal(history.SelectedRun!.SessionId, Assert.Single(harness.Clipboard.Texts));

        history.SelectedRun = history.Runs.First(run => !run.HasSessionId);
        Assert.False(history.CopySessionIdCommand.CanExecute(null));
    }

    [Fact]
    public async Task DeleteRemovesTheRunAndRefreshes()
    {
        using var harness = new ViewModelHarness();
        await SeedAsync(harness);

        using var history = harness.CreateHistory();
        await history.InitializeAsync();

        var target = history.Runs[0];
        history.SelectedRun = target;

        await history.DeleteRunCommand.ExecuteAsync(null);

        Assert.Equal(target.Id, Assert.Single(harness.HistoryEditor.Deleted));
        Assert.Null(history.SelectedRun);
    }

    [Fact]
    public async Task OpenTranscriptFileAsksTheShellForTheRunsLog()
    {
        using var harness = new ViewModelHarness();
        await SeedAsync(harness);

        using var history = harness.CreateHistory();
        await history.InitializeAsync();

        var target = history.Runs[0];
        history.SelectedRun = target;
        history.OpenTranscriptFileCommand.Execute(null);

        Assert.Equal(harness.History.TranscriptPath(target.Id), Assert.Single(harness.Shell.Opened));
    }

    [Fact]
    public async Task TheGridFormatsUsageCostAndDuration()
    {
        using var harness = new ViewModelHarness();
        await SeedAsync(harness);

        using var history = harness.CreateHistory();
        await history.InitializeAsync();

        var row = history.Runs.First(run => run.CostUsd is not null);
        Assert.Equal("9m", row.DurationText);
        Assert.Equal("72% (5h) · 41% (7d)", row.UsageAtStartText);
        Assert.StartsWith("$", row.CostText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyHistoryIsNotAnError()
    {
        using var harness = new ViewModelHarness();
        using var history = harness.CreateHistory();

        await history.InitializeAsync();

        Assert.Equal(0, history.TotalRunCount);
        Assert.True(history.IsEmpty);
    }
}
