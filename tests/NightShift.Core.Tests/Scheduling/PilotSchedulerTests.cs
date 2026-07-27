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

public sealed class PilotSchedulerTests : IDisposable
{
    static readonly DateTimeOffset Start = new(2026, 7, 26, 22, 0, 0, TimeSpan.Zero);

    readonly TempDirectory _appData = new();
    readonly TempDirectory _project = new();
    readonly FakeTimeProvider _time = new(Start);
    readonly AppPaths _paths;
    readonly RunHistoryStore _history;
    readonly PilotStateStore _stateStore;
    readonly RunGate _gate = new();
    readonly StubRunner _runner = new();

    PilotSettings _settings;

    public PilotSchedulerTests()
    {
        _paths = new AppPaths(_appData.Path);
        _history = new RunHistoryStore(_paths, NullLogger<RunHistoryStore>.Instance);
        _stateStore = new PilotStateStore(_paths, NullLogger<PilotStateStore>.Instance);
        _settings = new PilotSettings
        {
            ProjectDirectory = _project.Path,
            IntervalMinutes = 60,
            ThresholdPercent = 90,
        };
    }

    sealed class StubRunner : IClaudeRunner
    {
        readonly List<TaskCompletionSource<RunRecord>> _pending = [];

        public int Calls { get; private set; }

        public bool BlockUntilReleased { get; set; }

        public RunOutcome NextOutcome { get; set; } = RunOutcome.Success;

        public DateTimeOffset? NextRateLimitResetsAt { get; set; }

        public bool NextIsResumable { get; set; }

        /// <summary>When set, a blocked run honours cancellation the way a real runner does.</summary>
        public bool ObserveCancellation { get; set; }

        public LaunchMode Mode => LaunchMode.Headless;

        public Task<RunRecord> RunAsync(
            PilotSettings settings,
            IProgress<RunProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            var record = RunRecord.Start(DateTimeOffset.UnixEpoch) with
            {
                Outcome = NextOutcome,
                RateLimitResetsAt = NextRateLimitResetsAt,
                IsResumable = NextIsResumable,
                SessionId = "stub-session",
            };

            if (!BlockUntilReleased)
            {
                return Task.FromResult(record);
            }

            var source = new TaskCompletionSource<RunRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Add(source);

            if (ObserveCancellation)
            {
                cancellationToken.Register(() => source.TrySetCanceled(cancellationToken));
            }

            return source.Task;
        }

        public void ReleaseAll()
        {
            foreach (var pending in _pending)
            {
                pending.TrySetResult(RunRecord.Start(DateTimeOffset.UnixEpoch) with { Outcome = RunOutcome.Success });
            }

            _pending.Clear();
        }
    }

    ISettingsStore SettingsStore()
    {
        var store = Substitute.For<ISettingsStore>();
        store.Current.Returns(_ => _settings);
        return store;
    }

    CompositeUsageProvider UsageProvider(UsageSnapshot snapshot)
    {
        var provider = Substitute.For<IUsageProvider>();
        provider.Source.Returns(UsageSource.OAuth);
        provider.GetUsageAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(snapshot));

        return new CompositeUsageProvider(
            [provider],
            SettingsStore(),
            NullLogger<CompositeUsageProvider>.Instance,
            _time);
    }

    static UsageSnapshot Usage(double fiveHour, DateTimeOffset? resetsAt = null) =>
        new(new UsageWindow(fiveHour, resetsAt), null, null, null, UsageSource.OAuth, false, Start);

    /// <summary>A preflight that lets every cycle through, so these tests isolate the §6 rule.</summary>
    IPreflightChecker PassingPreflight(bool canRun = true)
    {
        var checker = Substitute.For<IPreflightChecker>();
        var checks = canRun
            ? new List<PreflightCheck>()
            : [new PreflightCheck(
                PreflightCheckId.PlanFile,
                "Plan file",
                PreflightStatus.Error,
                "No plan.md in the project directory.")];

        checker.CheckAsync(Arg.Any<PilotSettings>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PreflightResult(checks, Start, null, null, null)));
        return checker;
    }

    PilotScheduler CreateScheduler(UsageSnapshot usage, IPreflightChecker? preflight = null) =>
        new(
            SettingsStore(),
            UsageProvider(usage),
            preflight ?? PassingPreflight(),
            _gate,
            _stateStore,
            _history,
            [_runner],
            NullLogger<PilotScheduler>.Instance,
            _time);

    [Fact]
    public async Task Usage_below_the_threshold_runs()
    {
        var completed = await CreateScheduler(Usage(20)).RunNowAsync();

        Assert.True(completed.Decision.ShouldRun);
        Assert.Equal(1, _runner.Calls);
        Assert.NotNull(completed.Record);
    }

    [Fact]
    public async Task Usage_over_the_threshold_skips_and_launches_nothing()
    {
        var completed = await CreateScheduler(Usage(95)).RunNowAsync();

        Assert.False(completed.Decision.ShouldRun);
        Assert.Equal(SkipReason.OverThreshold, completed.Decision.Reason);
        Assert.Equal(0, _runner.Calls);
    }

    [Fact]
    public async Task Force_run_bypasses_the_usage_check()
    {
        var completed = await CreateScheduler(Usage(99)).RunNowAsync(bypassUsageCheck: true);

        Assert.True(completed.Decision.ShouldRun);
        Assert.Equal(1, _runner.Calls);
        Assert.Contains("bypassed", completed.Decision.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_now_still_honours_the_usage_check()
    {
        var completed = await CreateScheduler(Usage(99)).RunNowAsync();

        Assert.False(completed.Decision.ShouldRun);
        Assert.Equal(0, _runner.Calls);
    }

    [Fact]
    public async Task A_run_in_flight_makes_the_next_cycle_skip_rather_than_queue()
    {
        // plan.md §11.5: never queue. A long run must cause skips, not a stampede when it finishes.
        _runner.BlockUntilReleased = true;
        var scheduler = CreateScheduler(Usage(20));

        var first = scheduler.RunNowAsync();
        await Task.Delay(50);

        var second = await scheduler.RunNowAsync();

        Assert.Equal(SkipReason.AlreadyRunning, second.Decision.Reason);
        Assert.Equal(1, _runner.Calls);

        _runner.ReleaseAll();
        await first;
    }

    [Fact]
    public async Task A_paused_pilot_skips_scheduled_cycles_but_still_obeys_an_explicit_run()
    {
        var scheduler = CreateScheduler(Usage(20));
        scheduler.Pause();

        var manual = await scheduler.RunNowAsync();

        Assert.True(manual.Decision.ShouldRun);
        Assert.False(scheduler.IsEnabled);
    }

    [Fact]
    public async Task Skips_are_recorded_in_history_so_a_quiet_night_is_explained()
    {
        await CreateScheduler(Usage(95)).RunNowAsync();

        var records = await _history.ReadAllAsync();
        var skip = Assert.Single(records);
        Assert.Equal(RunOutcome.Skipped, skip.Outcome);
        Assert.Equal(SkipReason.OverThreshold, skip.SkipReason);
        Assert.NotNull(skip.UsageAtStart);
    }

    [Fact]
    public async Task A_runner_that_throws_is_recorded_as_a_failed_run_not_a_crash()
    {
        var throwing = Substitute.For<IClaudeRunner>();
        throwing.Mode.Returns(LaunchMode.Headless);
        throwing.RunAsync(Arg.Any<PilotSettings>(), Arg.Any<IProgress<RunProgress>>(), Arg.Any<CancellationToken>())
            .Returns<Task<RunRecord>>(_ => throw new InvalidOperationException("boom"));

        var scheduler = new PilotScheduler(
            SettingsStore(),
            UsageProvider(Usage(20)),
            PassingPreflight(),
            _gate,
            _stateStore,
            _history,
            [throwing],
            NullLogger<PilotScheduler>.Instance,
            _time);

        var completed = await scheduler.RunNowAsync();

        Assert.Equal(RunOutcome.Failed, completed.Record!.Outcome);
        Assert.Contains("boom", completed.Record.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_runner_for_the_configured_launch_mode_is_a_skip_not_an_exception()
    {
        _settings = _settings with { LaunchMode = LaunchMode.VisibleTerminal };

        var completed = await CreateScheduler(Usage(20)).RunNowAsync();

        Assert.Equal(SkipReason.PreflightFailed, completed.Decision.Reason);
    }

    [Fact]
    public async Task The_schedule_survives_a_restart()
    {
        var scheduler = CreateScheduler(Usage(20));
        await scheduler.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        var scheduled = scheduler.NextRunAt;
        await scheduler.StopAsync(CancellationToken.None);

        Assert.NotNull(scheduled);
        Assert.Equal(Start.AddMinutes(60), scheduled);

        var reloaded = await _stateStore.LoadAsync();
        Assert.Equal(scheduled, reloaded.NextRunAtUtc);
    }

    [Fact]
    public async Task A_blocked_cycle_reschedules_just_after_the_window_resets()
    {
        // plan.md §6 step 5: when the threshold blocks a cycle, resume just after the window rolls
        // over instead of waiting out a whole interval. Only the scheduled path reschedules — a
        // manual "Run now" must not move the user's cadence.
        var resetsAt = Start.AddMinutes(70);
        var scheduler = CreateScheduler(Usage(95, resetsAt));

        await scheduler.StartAsync(CancellationToken.None);
        await Task.Delay(100);

        // Fire the scheduled tick an hour in.
        _time.Advance(TimeSpan.FromMinutes(60));
        await Task.Delay(200);
        await scheduler.StopAsync(CancellationToken.None);

        // Without the reset-aware rule the next check would be at Start+120.
        Assert.Equal(resetsAt.AddMinutes(1), scheduler.NextRunAt);
    }

    [Fact]
    public async Task Startup_does_not_run_by_default()
    {
        var scheduler = CreateScheduler(Usage(20));

        await scheduler.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Equal(0, _runner.Calls);
    }

    [Fact]
    public async Task Startup_runs_when_RunOnStartup_is_set()
    {
        _settings = _settings with { RunOnStartup = true };
        var scheduler = CreateScheduler(Usage(20));

        await scheduler.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Equal(1, _runner.Calls);
    }

    [Fact]
    public async Task A_missed_schedule_runs_immediately_on_startup()
    {
        await _stateStore.SaveAsync(new PilotState { NextRunAtUtc = Start.AddMinutes(-30) });
        var scheduler = CreateScheduler(Usage(20));

        await scheduler.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Equal(1, _runner.Calls);
    }

    [Fact]
    public async Task The_first_tick_is_anchored_to_the_next_reset_without_waiting_for_a_cycle()
    {
        // A reset 20 minutes out beats the 60-minute interval, and must be picked up at startup
        // rather than only after the first cycle has run.
        var resetsAt = Start.AddMinutes(20);
        var scheduler = CreateScheduler(Usage(20, resetsAt));

        await scheduler.StartAsync(CancellationToken.None);
        await Task.Delay(150);
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Equal(resetsAt.AddMinutes(1), scheduler.NextRunAt);
    }

    [Fact]
    public async Task A_reset_beyond_the_interval_never_delays_the_ordinary_cadence()
    {
        // Alignment may only ever move a check earlier.
        var scheduler = CreateScheduler(Usage(20, Start.AddHours(4)));

        await scheduler.StartAsync(CancellationToken.None);
        await Task.Delay(150);
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Equal(Start.AddMinutes(60), scheduler.NextRunAt);
    }

    [Fact]
    public async Task Alignment_can_be_turned_off()
    {
        _settings = _settings with { AlignToQuotaReset = false };
        var scheduler = CreateScheduler(Usage(20, Start.AddMinutes(20)));

        await scheduler.StartAsync(CancellationToken.None);
        await Task.Delay(150);
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Equal(Start.AddMinutes(60), scheduler.NextRunAt);
    }

    [Fact]
    public async Task The_grace_period_is_configurable()
    {
        _settings = _settings with { QuotaResetGraceMinutes = 5 };
        var resetsAt = Start.AddMinutes(20);
        var scheduler = CreateScheduler(Usage(20, resetsAt));

        await scheduler.StartAsync(CancellationToken.None);
        await Task.Delay(150);
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Equal(resetsAt.AddMinutes(5), scheduler.NextRunAt);
    }

    [Fact]
    public async Task A_blocked_cycle_waits_for_its_own_window_not_an_earlier_unrelated_one()
    {
        // Blocked by the weekly window: waking when the 5-hour window resets would only produce
        // another skip, so the blocking window's reset wins even though it is later.
        var fiveHourReset = Start.AddMinutes(70);
        var sevenDayReset = Start.AddMinutes(90);
        var snapshot = new UsageSnapshot(
            new UsageWindow(10, fiveHourReset),
            new UsageWindow(96, sevenDayReset),
            null,
            null,
            UsageSource.OAuth,
            false,
            Start);

        var scheduler = CreateScheduler(snapshot);

        await scheduler.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        _time.Advance(TimeSpan.FromMinutes(60));
        await Task.Delay(200);
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Equal(sevenDayReset.AddMinutes(1), scheduler.NextRunAt);
    }

    [Fact]
    public async Task A_rate_limited_run_pushes_the_next_check_out_to_the_reset_even_past_the_interval()
    {
        // The one case where alignment moves a check LATER: the run itself proved the window is
        // spent, so ticking hourly against it would only produce skips.
        var quotaReturnsAt = Start.AddHours(4);
        _runner.NextOutcome = RunOutcome.RateLimited;
        _runner.NextRateLimitResetsAt = quotaReturnsAt;

        var scheduler = CreateScheduler(Usage(20));
        await scheduler.StartAsync(CancellationToken.None);
        await Task.Delay(100);

        _time.Advance(TimeSpan.FromMinutes(60));
        await Task.Delay(200);
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Equal(quotaReturnsAt.AddMinutes(1), scheduler.NextRunAt);
    }

    [Fact]
    public async Task A_distant_reported_reset_is_capped_because_resets_at_cannot_be_trusted()
    {
        // seven_day.resets_at reports when the oldest tokens age out (~7 days), not when quota
        // returns; the counter actually resets on a ~72h cycle. Sleeping on that timestamp would
        // idle the pilot for a week. Independently measured at 71.9h / 72.6h / 72.5h.
        _runner.NextOutcome = RunOutcome.RateLimited;
        _runner.NextRateLimitResetsAt = Start.AddDays(7);
        _settings = _settings with { MaxQuotaWaitHours = 6 };

        var scheduler = CreateScheduler(Usage(20));
        await scheduler.StartAsync(CancellationToken.None);
        await Task.Delay(100);

        _time.Advance(TimeSpan.FromMinutes(60));
        await Task.Delay(200);
        await scheduler.StopAsync(CancellationToken.None);

        // One hour has elapsed, so the cap lands six hours after that.
        Assert.Equal(Start.AddMinutes(60).AddHours(6), scheduler.NextRunAt);
    }

    [Fact]
    public async Task A_reset_within_the_cap_is_honoured_exactly()
    {
        _runner.NextOutcome = RunOutcome.RateLimited;
        _runner.NextRateLimitResetsAt = Start.AddMinutes(90);
        _settings = _settings with { MaxQuotaWaitHours = 6 };

        var scheduler = CreateScheduler(Usage(20));
        await scheduler.StartAsync(CancellationToken.None);
        await Task.Delay(100);

        _time.Advance(TimeSpan.FromMinutes(60));
        await Task.Delay(200);
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Equal(Start.AddMinutes(91), scheduler.NextRunAt);
    }

    [Fact]
    public async Task A_persisted_quota_wait_is_capped_on_restart_too()
    {
        await _stateStore.SaveAsync(new PilotState
        {
            QuotaResumesAtUtc = Start.AddDays(7),
            NextRunAtUtc = Start.AddMinutes(-5),
        });
        _settings = _settings with { MaxQuotaWaitHours = 6 };

        var scheduler = CreateScheduler(Usage(20));
        await scheduler.StartAsync(CancellationToken.None);
        await Task.Delay(150);
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Equal(Start.AddHours(6), scheduler.NextRunAt);
        Assert.Equal(0, _runner.Calls);
    }

    [Fact]
    public async Task A_rate_limited_run_without_a_reset_time_falls_back_to_the_interval()
    {
        _runner.NextOutcome = RunOutcome.RateLimited;
        _runner.NextRateLimitResetsAt = null;

        var scheduler = CreateScheduler(Usage(20));
        await scheduler.StartAsync(CancellationToken.None);
        await Task.Delay(100);

        _time.Advance(TimeSpan.FromMinutes(60));
        await Task.Delay(200);
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Equal(Start.AddMinutes(120), scheduler.NextRunAt);
    }

    [Fact]
    public async Task An_interrupted_session_and_its_quota_wait_survive_a_restart()
    {
        await _stateStore.SaveAsync(new PilotState
        {
            PendingResumeSessionId = "sess-interrupted",
            QuotaResumesAtUtc = Start.AddHours(3),
            NextRunAtUtc = Start.AddMinutes(-5),
        });

        // RunOnStartup is on, but the quota is known to be spent, so nothing may launch.
        _settings = _settings with { RunOnStartup = true };
        var scheduler = CreateScheduler(Usage(20));

        await scheduler.StartAsync(CancellationToken.None);
        await Task.Delay(150);
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Equal(0, _runner.Calls);
        Assert.Equal(Start.AddHours(3).AddMinutes(1), scheduler.NextRunAt);
    }

    [Fact]
    public async Task A_rate_limited_run_records_its_resumable_session_in_state()
    {
        _runner.NextOutcome = RunOutcome.RateLimited;
        _runner.NextRateLimitResetsAt = Start.AddHours(2);
        _runner.NextIsResumable = true;

        var scheduler = CreateScheduler(Usage(20));
        await scheduler.RunNowAsync();

        var state = await _stateStore.LoadAsync();
        Assert.Equal("stub-session", state.PendingResumeSessionId);
        Assert.Equal(Start.AddHours(2), state.QuotaResumesAtUtc);
    }

    [Fact]
    public async Task A_scheduled_run_can_be_stopped_by_the_user()
    {
        // plan.md §9.1's Stop button. Cancellation otherwise only travels down the token passed to
        // RunNowAsync, which a background cycle does not have — so without this the only way to stop
        // a scheduled run would be to kill the app.
        _runner.ObserveCancellation = true;
        _runner.BlockUntilReleased = true;
        var scheduler = CreateScheduler(Usage(20));

        var running = scheduler.RunNowAsync();
        await Task.Delay(80);

        Assert.True(scheduler.StopCurrentRun());

        var completed = await running;

        Assert.Equal(RunOutcome.Failed, completed.Record!.Outcome);
        Assert.Equal("Stopped by the user.", completed.Record.SkipDetail);

        var recorded = await _history.ReadAllAsync();
        Assert.Contains(recorded, r => r.SkipDetail == "Stopped by the user.");
    }

    [Fact]
    public void Stopping_when_nothing_is_running_is_a_no_op()
    {
        var scheduler = CreateScheduler(Usage(20));

        Assert.False(scheduler.StopCurrentRun());
    }

    [Fact]
    public async Task A_failing_preflight_blocks_the_cycle_before_usage_is_ever_fetched()
    {
        // plan.md §6 step 3: preflight comes before the usage lookup, so a misconfigured pilot
        // fails locally instead of spending a network call to learn it cannot launch.
        var completed = await CreateScheduler(Usage(20), PassingPreflight(canRun: false)).RunNowAsync();

        Assert.False(completed.Decision.ShouldRun);
        Assert.Equal(SkipReason.PreflightFailed, completed.Decision.Reason);
        Assert.Equal(0, _runner.Calls);
    }

    [Fact]
    public async Task A_preflight_that_throws_blocks_the_cycle_rather_than_killing_the_scheduler()
    {
        var throwing = Substitute.For<IPreflightChecker>();
        throwing.CheckAsync(Arg.Any<PilotSettings>(), Arg.Any<CancellationToken>())
            .Returns<Task<PreflightResult>>(_ => throw new InvalidOperationException("preflight bug"));

        var completed = await CreateScheduler(Usage(20), throwing).RunNowAsync();

        Assert.Equal(SkipReason.PreflightFailed, completed.Decision.Reason);
        Assert.Equal(0, _runner.Calls);
    }

    [Fact]
    public async Task Even_a_forced_run_will_not_launch_when_preflight_says_it_cannot()
    {
        // Force run bypasses the *usage* gate, not the "there is no plan file" gate.
        var completed = await CreateScheduler(Usage(20), PassingPreflight(canRun: false))
            .RunNowAsync(bypassUsageCheck: true);

        Assert.Equal(SkipReason.PreflightFailed, completed.Decision.Reason);
        Assert.Equal(0, _runner.Calls);
    }

    [Fact]
    public async Task Across_many_ticks_it_runs_and_skips_as_the_usage_figure_moves()
    {
        // plan.md Phase 4 acceptance: at a 1-minute interval with a stubbed provider, the
        // run/skip decisions must be correct across at least five ticks.
        _settings = _settings with { IntervalMinutes = 5 };

        var utilization = 20d;
        var provider = Substitute.For<IUsageProvider>();
        provider.Source.Returns(UsageSource.OAuth);
        provider.GetUsageAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(Usage(utilization)));

        var composite = new CompositeUsageProvider(
            [provider],
            SettingsStore(),
            NullLogger<CompositeUsageProvider>.Instance,
            _time);

        var decisions = new List<CycleDecision>();
        var scheduler = new PilotScheduler(
            SettingsStore(),
            composite,
            PassingPreflight(),
            _gate,
            _stateStore,
            _history,
            [_runner],
            NullLogger<PilotScheduler>.Instance,
            _time);
        scheduler.CycleCompleted += (_, c) =>
        {
            if (c.Trigger == CycleTrigger.Scheduled)
            {
                decisions.Add(c.Decision);
            }
        };

        await scheduler.StartAsync(CancellationToken.None);
        await Task.Delay(100);

        // below, below, above, above, below, below
        double[] sequence = [20, 45, 95, 91, 30, 10];
        foreach (var value in sequence)
        {
            utilization = value;
            _time.Advance(TimeSpan.FromMinutes(5));
            await Task.Delay(120);
        }

        await scheduler.StopAsync(CancellationToken.None);

        Assert.Equal(sequence.Length, decisions.Count);
        Assert.Equal(
            sequence.Select(v => v < 90).ToArray(),
            decisions.Select(d => d.ShouldRun).ToArray());
        Assert.Equal(sequence.Count(v => v < 90), _runner.Calls);

        // Every skip is explained, and every blocked one names the threshold.
        Assert.All(decisions.Where(d => !d.ShouldRun), d =>
        {
            Assert.Equal(SkipReason.OverThreshold, d.Reason);
            Assert.Contains("threshold", d.Detail, StringComparison.Ordinal);
        });
    }

    public void Dispose()
    {
        _appData.Dispose();
        _project.Dispose();
    }
}
