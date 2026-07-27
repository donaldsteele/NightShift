using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NightShift.Core.Configuration;
using NightShift.Core.Execution;
using NightShift.Core.History;
using NightShift.Core.Preflight;
using NightShift.Core.Scheduling;
using NightShift.Core.Usage;
using NSubstitute;

namespace NightShift.Core.Tests.Scheduling;

/// <summary>
/// A plan with nothing left to do must not launch Claude.
/// </summary>
/// <remarks>
/// <see cref="PreflightChecker"/> reports a finished plan as an amber row, and amber rows never
/// stop a run — so a fully ticked plan launched on every interval, spending a slot and a chunk of
/// the weekly quota to be told there was nothing to do. The check named that exact cost in its own
/// remarks and had no way to act on it. This is the acting.
/// </remarks>
public sealed class NoWorkLeftTests : IDisposable
{
    static readonly DateTimeOffset Start = new(2026, 7, 27, 22, 0, 0, TimeSpan.Zero);

    readonly TempDirectory _appData = new();
    readonly TempDirectory _project = new();
    readonly FakeTimeProvider _time = new(Start);
    readonly RunHistoryStore _history;
    readonly PilotStateStore _stateStore;
    readonly RunGate _gate = new();
    readonly CountingRunner _runner = new();
    readonly PilotSettings _settings;

    IUsageProvider? _provider;

    public NoWorkLeftTests()
    {
        var paths = new AppPaths(_appData.Path);
        _history = new RunHistoryStore(paths, NullLogger<RunHistoryStore>.Instance);
        _stateStore = new PilotStateStore(paths, NullLogger<PilotStateStore>.Instance);
        _settings = new PilotSettings
        {
            ProjectDirectory = _project.Path,
            IntervalMinutes = 60,
            ThresholdPercent = 90,
        };
    }

    sealed class CountingRunner : IClaudeRunner
    {
        public int Calls { get; private set; }

        public LaunchMode Mode => LaunchMode.Headless;

        public Task<RunRecord> RunAsync(
            PilotSettings settings,
            IProgress<RunProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(RunRecord.Start(Start) with { Outcome = RunOutcome.Success });
        }
    }

    PilotScheduler Create(PlanItemCounts? counts)
    {
        var settings = Substitute.For<ISettingsStore>();
        settings.Current.Returns(_ => _settings);

        var preflight = Substitute.For<IPreflightChecker>();
        preflight.CheckAsync(Arg.Any<PilotSettings>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PreflightResult([], Start, counts)));

        var provider = _provider = Substitute.For<IUsageProvider>();
        provider.Source.Returns(UsageSource.OAuth);
        provider.GetUsageAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(
            Task.FromResult(new UsageSnapshot(
                new UsageWindow(10, null), null, null, null, UsageSource.OAuth, false, Start)));

        return new PilotScheduler(
            settings,
            new CompositeUsageProvider([provider], settings, NullLogger<CompositeUsageProvider>.Instance, _time),
            preflight,
            _gate,
            _stateStore,
            _history,
            [_runner],
            NullLogger<PilotScheduler>.Instance,
            _time);
    }

    [Fact]
    public async Task A_plan_with_nothing_left_does_not_launch_Claude()
    {
        var completed = await Create(new PlanItemCounts(30, 0, 0)).RunNowAsync();

        Assert.Equal(0, _runner.Calls);
        Assert.False(completed.Decision.ShouldRun);
        Assert.Equal(SkipReason.NoWorkLeft, completed.Decision.Reason);
    }

    [Fact]
    public async Task A_finished_milestone_plan_does_not_launch_Claude_either()
    {
        var counts = new PlanItemCounts(16, 0, 0, PlanFormat.Milestone);

        var completed = await Create(counts).RunNowAsync();

        Assert.Equal(0, _runner.Calls);
        Assert.Equal(SkipReason.NoWorkLeft, completed.Decision.Reason);
        Assert.Contains("16 of 16 milestones complete", completed.Decision.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_skip_says_how_many_are_blocked_rather_than_just_stopping()
    {
        // Nine items left but every one of them marked blocked is a different situation from a
        // finished plan, and the user has to be able to tell them apart from the History row.
        var completed = await Create(new PlanItemCounts(4, 0, 9)).RunNowAsync();

        Assert.Equal(SkipReason.NoWorkLeft, completed.Decision.Reason);
        Assert.Contains("9 items are marked blocked", completed.Decision.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_item_left_still_runs()
    {
        var completed = await Create(new PlanItemCounts(29, 1, 0)).RunNowAsync();

        Assert.Equal(1, _runner.Calls);
        Assert.True(completed.Decision.ShouldRun);
    }

    [Fact]
    public async Task A_plan_with_no_items_at_all_still_runs()
    {
        // "No checkboxes yet" is not "finished". A plan being written, or one whose format is
        // detected as the other kind, must not silently stop the pilot forever.
        var completed = await Create(new PlanItemCounts(0, 0, 0)).RunNowAsync();

        Assert.Equal(1, _runner.Calls);
        Assert.True(completed.Decision.ShouldRun);
    }

    [Fact]
    public async Task An_unreadable_plan_still_runs()
    {
        // Null counts mean preflight could not read the file. Refusing to run then would turn a
        // transient read failure into a pilot that never starts again.
        var completed = await Create(null).RunNowAsync();

        Assert.Equal(1, _runner.Calls);
    }

    [Fact]
    public async Task Force_run_goes_through_anyway()
    {
        // Every other gate lets Force run past, and the user asking for it has already been told
        // on the card that there is nothing to do.
        var completed = await Create(new PlanItemCounts(30, 0, 0)).RunNowAsync(bypassUsageCheck: true);

        Assert.Equal(1, _runner.Calls);
        Assert.True(completed.Decision.ShouldRun);
    }

    [Fact]
    public async Task The_skip_is_recorded_in_history_so_the_morning_shows_why()
    {
        await Create(new PlanItemCounts(30, 0, 0)).RunNowAsync();

        var records = await _history.ReadAllAsync();
        var skip = Assert.Single(records);

        Assert.Equal(RunOutcome.Skipped, skip.Outcome);
        Assert.Equal(SkipReason.NoWorkLeft, skip.SkipReason);
    }

    [Fact]
    public async Task Usage_is_still_read_on_a_cycle_that_finds_nothing_to_do()
    {
        // The check sits after the usage read on purpose: a pilot parked on a finished plan must
        // not also freeze its own gauges, which is what "the number never refreshes" looked like.
        await Create(new PlanItemCounts(30, 0, 0)).RunNowAsync();

        await _provider!.Received().GetUsageAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    public void Dispose()
    {
        _appData.Dispose();
        _project.Dispose();
    }
}
