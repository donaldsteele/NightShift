using NightShift.Core.Configuration;
using NightShift.Core.History;
using NightShift.Desktop.ViewModels;

namespace NightShift.Desktop.Tests;

/// <summary>
/// The status pill, verbatim from plan.md §9.1 plus the §6.1 mid-run quota state.
/// </summary>
/// <remarks>
/// The scheduler here is real: <c>CycleCompleted</c> is a field-like event, so the only way to make
/// one arrive is to make a cycle happen. <see cref="ViewModelHarness"/> fakes everything below it.
/// </remarks>
public class MainWindowViewModelTests
{
    [Fact]
    public void IdleWithNothingScheduledJustSaysIdle()
    {
        using var harness = new ViewModelHarness();
        using var main = harness.CreateMainWindow();

        main.RefreshStatus();

        Assert.Equal(PilotStatusKind.Idle, main.StatusKind);
        Assert.Equal("Idle", main.StatusText);
    }

    [Fact]
    public async Task IdleCountsDownToTheNextScheduledRun()
    {
        using var harness = new ViewModelHarness(new PilotSettings { IntervalMinutes = 42 });
        using var main = harness.CreateMainWindow();

        // Starting the background service is what populates NextRunAt; the fake clock means the
        // wait loop never actually ticks.
        await harness.Scheduler.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(SpinWait.SpinUntil(() => harness.Scheduler.NextRunAt is not null, 5000));

            main.RefreshStatus();

            Assert.Equal(PilotStatusKind.Idle, main.StatusKind);
            Assert.Equal("Idle · next run in 42m", main.StatusText);
            Assert.Equal("NightShift — next run in 42m", main.TrayTooltip);
        }
        finally
        {
            await harness.Scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RunningShowsTheElapsedTime()
    {
        using var harness = new ViewModelHarness();
        harness.PreflightPasses();
        harness.UsageProvider.Snapshot = harness.Snapshot(10);
        harness.Runner.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var dashboard = harness.CreateDashboard();
        using var main = harness.CreateMainWindow(dashboard);

        var run = dashboard.RunNowCommand.ExecuteAsync(null);
        await harness.Runner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        harness.Time.Advance(TimeSpan.FromMinutes(3));
        main.RefreshStatus();

        Assert.Equal(PilotStatusKind.Running, main.StatusKind);
        Assert.Equal("Running — 3m elapsed", main.StatusText);

        harness.Runner.Gate.SetResult();
        await run;
    }

    [Fact]
    public void PausedSaysPaused()
    {
        using var harness = new ViewModelHarness();
        using var dashboard = harness.CreateDashboard();
        using var main = harness.CreateMainWindow(dashboard);

        dashboard.PauseCommand.Execute(null);
        main.RefreshStatus();

        Assert.Equal(PilotStatusKind.Paused, main.StatusKind);
        Assert.Equal("Paused", main.StatusText);

        dashboard.ResumeCommand.Execute(null);
        main.RefreshStatus();

        Assert.Equal(PilotStatusKind.Idle, main.StatusKind);
    }

    [Fact]
    public async Task OverThresholdSaysBlockedWithThePercentage()
    {
        using var harness = new ViewModelHarness(new PilotSettings { ThresholdPercent = 90 });
        harness.PreflightPasses();
        harness.UsageProvider.Snapshot = harness.Snapshot(94);

        using var dashboard = harness.CreateDashboard();
        using var main = harness.CreateMainWindow(dashboard);

        await harness.Scheduler.RunNowAsync(bypassUsageCheck: false);

        Assert.Equal(PilotStatusKind.Blocked, main.StatusKind);
        Assert.Equal("Blocked: usage 94%", main.StatusText);
    }

    [Fact]
    public async Task UsageUnavailableSaysBlockedToo()
    {
        using var harness = new ViewModelHarness();
        harness.PreflightPasses();
        harness.UsageProvider.Snapshot =
            UsageSnapshotHelper.Unavailable("the endpoint returned 401", harness.Time.GetUtcNow());

        using var dashboard = harness.CreateDashboard();
        using var main = harness.CreateMainWindow(dashboard);

        await harness.Scheduler.RunNowAsync(bypassUsageCheck: false);

        Assert.Equal(PilotStatusKind.Blocked, main.StatusKind);
        Assert.Equal("Blocked: usage unavailable", main.StatusText);
    }

    [Fact]
    public async Task AFailedPreflightSaysBlockedPreflight()
    {
        using var harness = new ViewModelHarness();
        harness.PreflightBlocks();

        using var dashboard = harness.CreateDashboard();
        using var main = harness.CreateMainWindow(dashboard);

        await harness.Scheduler.RunNowAsync(bypassUsageCheck: false);

        Assert.Equal(PilotStatusKind.Blocked, main.StatusKind);
        Assert.Equal("Blocked: preflight", main.StatusText);
    }

    [Fact]
    public async Task QuotaExhaustedMidRunIsItsOwnStateWithAKnownReturnTime()
    {
        using var harness = new ViewModelHarness();
        harness.PreflightPasses();
        harness.UsageProvider.Snapshot = harness.Snapshot(10);

        var resetsAt = harness.Time.GetUtcNow().AddMinutes(134);
        harness.Runner.Record = RunRecord.Start(harness.Time.GetUtcNow()) with
        {
            EndedAt = harness.Time.GetUtcNow(),
            Outcome = RunOutcome.RateLimited,
            RateLimitResetsAt = resetsAt,
            IsResumable = true,
            SessionId = "sess-42",
        };

        using var dashboard = harness.CreateDashboard();
        using var main = harness.CreateMainWindow(dashboard);

        await harness.Scheduler.RunNowAsync(bypassUsageCheck: false);

        Assert.True(dashboard.IsRateLimited);
        Assert.Equal(resetsAt, dashboard.RateLimitResetsAt);
        Assert.Equal("sess-42", dashboard.PendingResumeSessionId);

        Assert.Equal(PilotStatusKind.RateLimited, main.StatusKind);
        Assert.Equal("Rate limited · quota returns in 2h 14m", main.StatusText);
        Assert.Contains("resumed, not restarted", dashboard.RateLimitMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuotaExhaustedWithNoReportedResetStillShowsTheState()
    {
        using var harness = new ViewModelHarness();
        harness.PreflightPasses();
        harness.UsageProvider.Snapshot = harness.Snapshot(10);
        harness.Runner.Record = RunRecord.Start(harness.Time.GetUtcNow()) with
        {
            EndedAt = harness.Time.GetUtcNow(),
            Outcome = RunOutcome.RateLimited,
            RateLimitResetsAt = null,
        };

        using var dashboard = harness.CreateDashboard();
        using var main = harness.CreateMainWindow(dashboard);

        await harness.Scheduler.RunNowAsync(bypassUsageCheck: false);

        Assert.Equal(PilotStatusKind.RateLimited, main.StatusKind);
        Assert.Equal("Rate limited · waiting for quota", main.StatusText);
    }

    [Fact]
    public async Task TheQuotaBlockClearsOnceTheWindowHasRolledOver()
    {
        using var harness = new ViewModelHarness();
        harness.PreflightPasses();
        harness.UsageProvider.Snapshot = harness.Snapshot(10);
        harness.Runner.Record = RunRecord.Start(harness.Time.GetUtcNow()) with
        {
            EndedAt = harness.Time.GetUtcNow(),
            Outcome = RunOutcome.RateLimited,
            RateLimitResetsAt = harness.Time.GetUtcNow().AddMinutes(30),
        };

        using var dashboard = harness.CreateDashboard();
        using var main = harness.CreateMainWindow(dashboard);

        await harness.Scheduler.RunNowAsync(bypassUsageCheck: false);
        Assert.Equal(PilotStatusKind.RateLimited, main.StatusKind);

        harness.Time.Advance(TimeSpan.FromMinutes(31));
        main.Tick();

        Assert.False(dashboard.IsRateLimited);
        Assert.Equal(PilotStatusKind.Idle, main.StatusKind);
    }

    [Fact]
    public async Task PausingWinsOverAQuotaBlockBecauseTheUserAskedForIt()
    {
        using var harness = new ViewModelHarness();
        harness.PreflightPasses();
        harness.UsageProvider.Snapshot = harness.Snapshot(10);
        harness.Runner.Record = RunRecord.Start(harness.Time.GetUtcNow()) with
        {
            EndedAt = harness.Time.GetUtcNow(),
            Outcome = RunOutcome.RateLimited,
            RateLimitResetsAt = harness.Time.GetUtcNow().AddHours(2),
        };

        using var dashboard = harness.CreateDashboard();
        using var main = harness.CreateMainWindow(dashboard);

        await harness.Scheduler.RunNowAsync(bypassUsageCheck: false);
        dashboard.PauseCommand.Execute(null);
        main.RefreshStatus();

        Assert.Equal(PilotStatusKind.Paused, main.StatusKind);
    }

    [Fact]
    public void NavigationKeepsTheSectionAndTheRailSelectionInStep()
    {
        using var harness = new ViewModelHarness();
        using var main = harness.CreateMainWindow();

        Assert.Equal(NavigationSection.Dashboard, main.CurrentSection);
        Assert.Same(main.Dashboard, main.CurrentViewModel);

        main.NavigateCommand.Execute(NavigationSection.History);

        Assert.Equal(NavigationSection.History, main.CurrentSection);
        Assert.Same(main.History, main.CurrentViewModel);
        Assert.Equal(NavigationSection.History, main.SelectedNavigationItem.Section);

        main.SelectedNavigationItem = main.Sections[2];

        Assert.Equal(NavigationSection.Settings, main.CurrentSection);
        Assert.Same(main.Settings, main.CurrentViewModel);
    }
}

/// <summary>Convenience so tests do not repeat the unavailable-snapshot factory call.</summary>
static class UsageSnapshotHelper
{
    public static Core.Usage.UsageSnapshot Unavailable(string reason, DateTimeOffset at) =>
        Core.Usage.UsageSnapshot.Unavailable(reason, at);
}
