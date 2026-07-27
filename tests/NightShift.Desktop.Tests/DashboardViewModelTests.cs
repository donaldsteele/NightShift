using NightShift.Core.Configuration;
using NightShift.Core.Execution;
using NightShift.Core.Preflight;
using NightShift.Core.Usage;
using NightShift.Desktop.ViewModels;

namespace NightShift.Desktop.Tests;

/// <summary>
/// plan.md §9.1: preflight fix dispatch, the run buttons, the gauges and the live output pane.
/// </summary>
public class DashboardViewModelTests
{
    static PreflightResult WithFix(PreflightFixAction fix, PreflightCheckId id = PreflightCheckId.WorkspaceTrust) =>
        new(
            [new PreflightCheck(id, "Check", PreflightStatus.Error, "Something is wrong.") { Fix = fix }],
            DateTimeOffset.UnixEpoch,
            new PlanItemCounts(12, 18, 0));

    // ── Preflight fix dispatch ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AFixCoreOwnsIsHandedToCoreAndPreflightIsReRun()
    {
        using var harness = new ViewModelHarness();
        harness.Preflight.Result = WithFix(
            new PreflightFixAction(PreflightFixCommand.TrustProjectFolder, "Trust this folder", @"C:\code"));

        using var dashboard = harness.CreateDashboard();
        await dashboard.RunPreflightCommand.ExecuteAsync(null);
        var before = harness.Preflight.CheckCount;

        await dashboard.PreflightChecks[0].ApplyFixCommand.ExecuteAsync(null);

        var applied = Assert.Single(harness.FixApplier.Applied);
        Assert.Equal(PreflightFixCommand.TrustProjectFolder, applied.Command);
        Assert.Equal("fake: TrustProjectFolder", dashboard.LastFixMessage);
        Assert.True(harness.Preflight.CheckCount > before);
    }

    [Fact]
    public async Task AFailedCoreFixDoesNotReRunPreflight()
    {
        using var harness = new ViewModelHarness();
        harness.FixApplier.Outcome = PreflightFixOutcome.Failed;
        harness.Preflight.Result = WithFix(
            new PreflightFixAction(PreflightFixCommand.CreatePlanFile, "Create a starter plan.md", @"C:\code\plan.md"));

        using var dashboard = harness.CreateDashboard();
        await dashboard.RunPreflightCommand.ExecuteAsync(null);
        var before = harness.Preflight.CheckCount;

        await dashboard.PreflightChecks[0].ApplyFixCommand.ExecuteAsync(null);

        Assert.Single(harness.FixApplier.Applied);
        Assert.Equal(before, harness.Preflight.CheckCount);
    }

    [Fact]
    public async Task PickingTheProjectDirectoryIsTheAppsJobNotCores()
    {
        using var harness = new ViewModelHarness();
        harness.Picker.FolderResult = @"C:\code\NightShift";
        harness.Preflight.Result = WithFix(
            new PreflightFixAction(PreflightFixCommand.SetProjectDirectory, "Pick a directory…"),
            PreflightCheckId.ProjectDirectory);

        using var dashboard = harness.CreateDashboard();
        await dashboard.RunPreflightCommand.ExecuteAsync(null);

        await dashboard.PreflightChecks[0].ApplyFixCommand.ExecuteAsync(null);

        Assert.Empty(harness.FixApplier.Applied);
        Assert.Equal(1, harness.Picker.FolderCallCount);
        Assert.Equal(@"C:\code\NightShift", harness.Settings.Current.ProjectDirectory);
        Assert.Equal(@"C:\code\NightShift", dashboard.ProjectDirectory);
    }

    [Fact]
    public async Task CancellingThePickerChangesNothing()
    {
        using var harness = new ViewModelHarness();
        harness.Picker.FolderResult = null;
        harness.Preflight.Result = WithFix(
            new PreflightFixAction(PreflightFixCommand.SetProjectDirectory, "Pick a directory…"),
            PreflightCheckId.ProjectDirectory);

        using var dashboard = harness.CreateDashboard();
        await dashboard.RunPreflightCommand.ExecuteAsync(null);

        await dashboard.PreflightChecks[0].ApplyFixCommand.ExecuteAsync(null);

        Assert.Equal(0, harness.Settings.SaveCount);
    }

    [Fact]
    public async Task PickingTheClaudeExecutableGoesThroughTheFilePicker()
    {
        using var harness = new ViewModelHarness();
        harness.Picker.FileResult = @"C:\npm\claude.cmd";
        harness.Preflight.Result = WithFix(
            new PreflightFixAction(PreflightFixCommand.SetClaudeExecutablePath, "Set the Claude Code path…"),
            PreflightCheckId.ClaudeExecutable);

        using var dashboard = harness.CreateDashboard();
        await dashboard.RunPreflightCommand.ExecuteAsync(null);

        await dashboard.PreflightChecks[0].ApplyFixCommand.ExecuteAsync(null);

        Assert.Equal(1, harness.Picker.FileCallCount);
        Assert.Equal(@"C:\npm\claude.cmd", harness.Settings.Current.ClaudeExecutablePath);
    }

    [Fact]
    public async Task OpeningASettingsFileGoesToTheShell()
    {
        using var harness = new ViewModelHarness();
        var target = Path.Combine(harness.Root, "settings.json");
        harness.Preflight.Result = WithFix(
            new PreflightFixAction(PreflightFixCommand.OpenSettingsFile, "Open the settings file", target),
            PreflightCheckId.PermissionsAsk);

        using var dashboard = harness.CreateDashboard();
        await dashboard.RunPreflightCommand.ExecuteAsync(null);

        await dashboard.PreflightChecks[0].ApplyFixCommand.ExecuteAsync(null);

        Assert.Equal(target, Assert.Single(harness.Shell.Opened));
        Assert.Empty(harness.FixApplier.Applied);
    }

    [Fact]
    public async Task TheLoginFixExplainsRatherThanPretendingToLogIn()
    {
        using var harness = new ViewModelHarness();
        harness.Preflight.Result = WithFix(
            new PreflightFixAction(PreflightFixCommand.ShowLoginInstructions, "How to log in…"),
            PreflightCheckId.Credentials);

        using var dashboard = harness.CreateDashboard();
        await dashboard.RunPreflightCommand.ExecuteAsync(null);

        await dashboard.PreflightChecks[0].ApplyFixCommand.ExecuteAsync(null);

        Assert.True(dashboard.IsLoginInstructionsVisible);
        Assert.Equal(DashboardViewModel.LoginInstructions, dashboard.LoginInstructionsMessage);
        Assert.Empty(harness.FixApplier.Applied);

        dashboard.DismissLoginInstructionsCommand.Execute(null);
        Assert.False(dashboard.IsLoginInstructionsVisible);
    }

    [Fact]
    public async Task APillWithNoFixOffersNoFixCommand()
    {
        using var harness = new ViewModelHarness();
        harness.PreflightPasses();

        using var dashboard = harness.CreateDashboard();
        await dashboard.RunPreflightCommand.ExecuteAsync(null);

        var pill = Assert.Single(dashboard.PreflightChecks);
        Assert.False(pill.HasFix);
        Assert.False(pill.ApplyFixCommand.CanExecute(null));
    }

    [Fact]
    public async Task ThePreflightStripCarriesTheStatusAndThePlanCounts()
    {
        using var harness = new ViewModelHarness();
        harness.PreflightPasses();

        using var dashboard = harness.CreateDashboard();
        await dashboard.RunPreflightCommand.ExecuteAsync(null);

        Assert.Equal(PreflightStatus.Ok, dashboard.PreflightStatus);
        Assert.False(dashboard.HasPreflightErrors);
        Assert.Equal(12, dashboard.PlanCompletedCount);
        Assert.Equal(18, dashboard.PlanRemainingCount);
        Assert.Equal(30, dashboard.PlanTotalCount);
        Assert.Equal("12 of 30 items complete", dashboard.PlanItemsSummary);
        Assert.Equal(0.4, dashboard.PlanProgressFraction, 3);
    }

    // ── Buttons ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ForceRunWithoutConfirmationDoesNotRun()
    {
        using var harness = new ViewModelHarness();
        harness.PreflightPasses();
        harness.UsageProvider.Snapshot = harness.Snapshot(99);
        harness.Confirmation.Answer = false;

        using var dashboard = harness.CreateDashboard();
        await dashboard.ForceRunCommand.ExecuteAsync(null);

        Assert.Equal(1, harness.Confirmation.CallCount);
        Assert.False(harness.Runner.Entered.Task.IsCompleted);
        Assert.Equal("Force run cancelled.", dashboard.StatusMessage);
    }

    [Fact]
    public async Task ForceRunWithConfirmationBypassesTheUsageGate()
    {
        using var harness = new ViewModelHarness();
        harness.PreflightPasses();
        harness.UsageProvider.Snapshot = harness.Snapshot(99);
        harness.Confirmation.Answer = true;

        using var dashboard = harness.CreateDashboard();
        await dashboard.ForceRunCommand.ExecuteAsync(null);

        Assert.Equal(1, harness.Confirmation.CallCount);
        Assert.True(harness.Runner.Entered.Task.IsCompleted);
    }

    [Fact]
    public async Task RunNowStillHonoursTheUsageGate()
    {
        using var harness = new ViewModelHarness(new PilotSettings { ThresholdPercent = 90 });
        harness.PreflightPasses();
        harness.UsageProvider.Snapshot = harness.Snapshot(99);

        using var dashboard = harness.CreateDashboard();
        await dashboard.RunNowCommand.ExecuteAsync(null);

        Assert.False(harness.Runner.Entered.Task.IsCompleted);
        Assert.Contains("threshold", dashboard.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopRunIsOnlyOfferedWhileThisWindowOwnsARun()
    {
        using var harness = new ViewModelHarness();
        harness.PreflightPasses();
        harness.UsageProvider.Snapshot = harness.Snapshot(10);
        harness.Runner.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var dashboard = harness.CreateDashboard();
        Assert.False(dashboard.StopRunCommand.CanExecute(null));

        var run = dashboard.RunNowCommand.ExecuteAsync(null);
        await harness.Runner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(dashboard.StopRunCommand.CanExecute(null));
        dashboard.StopRunCommand.Execute(null);
        await run;

        Assert.Equal("Run stopped.", dashboard.StatusMessage);
        Assert.False(dashboard.StopRunCommand.CanExecute(null));
    }

    [Fact]
    public void PauseAndResumeFlipTheSchedulerAndTheirOwnCanExecute()
    {
        using var harness = new ViewModelHarness();
        using var dashboard = harness.CreateDashboard();

        Assert.True(dashboard.PauseCommand.CanExecute(null));
        Assert.False(dashboard.ResumeCommand.CanExecute(null));

        dashboard.PauseCommand.Execute(null);

        Assert.True(dashboard.IsPaused);
        Assert.False(harness.Scheduler.IsEnabled);
        Assert.False(dashboard.PauseCommand.CanExecute(null));
        Assert.True(dashboard.ResumeCommand.CanExecute(null));

        dashboard.ResumeCommand.Execute(null);

        Assert.False(dashboard.IsPaused);
        Assert.True(harness.Scheduler.IsEnabled);
    }

    [Fact]
    public async Task CopyOutputPutsTheBufferOnTheClipboard()
    {
        using var harness = new ViewModelHarness();
        using var dashboard = harness.CreateDashboard();

        await dashboard.CopyOutputCommand.ExecuteAsync(null);

        Assert.Single(harness.Clipboard.Texts);
        Assert.Equal("Output copied to the clipboard.", dashboard.StatusMessage);
    }

    // ── Gauges ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheGaugesRenderBothWindowsAndTheirResetCountdowns()
    {
        using var harness = new ViewModelHarness();
        var resetsAt = harness.Time.GetUtcNow().AddMinutes(134);
        harness.UsageProvider.Snapshot = new UsageSnapshot(
            new UsageWindow(72, resetsAt),
            new UsageWindow(41, harness.Time.GetUtcNow().AddDays(3)),
            null,
            null,
            UsageSource.OAuth,
            IsApproximate: false,
            harness.Time.GetUtcNow());

        using var dashboard = harness.CreateDashboard();
        await dashboard.RefreshUsageCommand.ExecuteAsync(null);

        Assert.Equal(DashboardViewModel.SessionGaugeCaption, dashboard.SessionGauge.Caption);
        Assert.Equal(72, dashboard.SessionGauge.Percent);
        Assert.False(dashboard.SessionGauge.IsUnavailable);
        Assert.False(dashboard.SessionGauge.IsApproximate);
        Assert.Equal("resets in 2h 14m", dashboard.SessionGauge.SubCaption);

        Assert.Equal(DashboardViewModel.WeeklyGaugeCaption, dashboard.WeeklyGauge.Caption);
        Assert.Equal(41, dashboard.WeeklyGauge.Percent);
        Assert.Equal("resets in 3d 00h", dashboard.WeeklyGauge.SubCaption);
    }

    [Fact]
    public async Task ACcusageSnapshotIsMarkedApproximate()
    {
        using var harness = new ViewModelHarness();
        harness.UsageProvider.Snapshot = harness.Snapshot(55, approximate: true);

        using var dashboard = harness.CreateDashboard();
        await dashboard.RefreshUsageCommand.ExecuteAsync(null);

        Assert.True(dashboard.SessionGauge.IsApproximate);
        Assert.True(dashboard.WeeklyGauge.IsApproximate);
    }

    [Fact]
    public async Task AnUnavailableSnapshotIsNotRenderedAsZeroPercent()
    {
        using var harness = new ViewModelHarness();
        harness.UsageProvider.Snapshot =
            UsageSnapshot.Unavailable("the endpoint returned 401", harness.Time.GetUtcNow());

        using var dashboard = harness.CreateDashboard();
        await dashboard.RefreshUsageCommand.ExecuteAsync(null);

        Assert.True(dashboard.SessionGauge.IsUnavailable);
        Assert.True(dashboard.WeeklyGauge.IsUnavailable);
        Assert.Contains("401", dashboard.SessionGauge.SubCaption, StringComparison.Ordinal);
    }

    [Fact]
    public void GaugesStartUnavailableRatherThanClaimingPlentyLeft()
    {
        using var harness = new ViewModelHarness();
        using var dashboard = harness.CreateDashboard();

        Assert.True(dashboard.SessionGauge.IsUnavailable);
        Assert.True(dashboard.WeeklyGauge.IsUnavailable);
    }

    // ── Live output pane ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StreamEventsFillThePaneAndTheExcludedOnesDoNot()
    {
        using var previous = new InlineSynchronizationContextScope();

        using var harness = new ViewModelHarness();
        harness.PreflightPasses();
        harness.UsageProvider.Snapshot = harness.Snapshot(10);
        harness.Runner.Events.AddRange(
        [
            new ClaudeTextDeltaEvent("{}", "Ticked item 12.\n"),
            new ClaudeInputJsonDeltaEvent("{}", "{\"file_path\":\"C:\\\\code\""),
            new ClaudeToolResultEvent("{}", "toolu_1", "42 files changed"),
            new ClaudeContentBlockStartEvent("{}", "tool_use") { ToolName = "Write" },
        ]);

        using var dashboard = harness.CreateDashboard();
        await dashboard.RunNowCommand.ExecuteAsync(null);
        dashboard.FlushOutput();

        Assert.Contains("Ticked item 12.", dashboard.OutputText, StringComparison.Ordinal);
        Assert.Contains($"{OutputPaneWriter.ToolUseMarker} Write", dashboard.OutputText, StringComparison.Ordinal);
        Assert.DoesNotContain("file_path", dashboard.OutputText, StringComparison.Ordinal);
        Assert.DoesNotContain("42 files changed", dashboard.OutputText, StringComparison.Ordinal);
        Assert.Equal(harness.Runner.Record.Id, dashboard.CurrentRunId);
        Assert.Equal(harness.History.TranscriptPath(harness.Runner.Record.Id), dashboard.CurrentTranscriptPath);
    }

    [Fact]
    public async Task ThePaneIsCappedAndSaysSoWhileTheFullTextStaysOnDisk()
    {
        using var previous = new InlineSynchronizationContextScope();

        using var harness = new ViewModelHarness();
        harness.PreflightPasses();
        harness.UsageProvider.Snapshot = harness.Snapshot(10);
        for (var i = 0; i < 50; i++)
        {
            harness.Runner.Events.Add(new ClaudeTextDeltaEvent("{}", $"line {i}\n"));
        }

        using var dashboard = harness.CreateDashboard(outputCapacityLines: 5);
        await dashboard.RunNowCommand.ExecuteAsync(null);
        dashboard.FlushOutput();

        Assert.Equal(5, dashboard.OutputCapacityLines);
        Assert.Equal(5, dashboard.OutputLineCount);
        Assert.Equal(45, dashboard.OutputDroppedLineCount);
        Assert.True(dashboard.IsOutputTruncated);
        Assert.DoesNotContain("line 0\n", dashboard.OutputText, StringComparison.Ordinal);
        Assert.Contains("line 49", dashboard.OutputText, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearOutputEmptiesThePane()
    {
        using var harness = new ViewModelHarness();
        using var dashboard = harness.CreateDashboard();

        dashboard.ClearOutputCommand.Execute(null);

        Assert.Equal(string.Empty, dashboard.OutputText);
        Assert.Equal(0, dashboard.OutputLineCount);
        Assert.False(dashboard.IsOutputTruncated);
    }

    // ── Project card ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheProjectCardFollowsTheSettingsStore()
    {
        using var harness = new ViewModelHarness();
        using var dashboard = harness.CreateDashboard();

        Assert.False(dashboard.HasProjectDirectory);
        Assert.False(dashboard.OpenProjectDirectoryCommand.CanExecute(null));

        await harness.Settings.SaveAsync(
            harness.Settings.Current with { ProjectDirectory = @"C:\code\NightShift" });

        Assert.Equal(@"C:\code\NightShift", dashboard.ProjectDirectory);
        Assert.True(dashboard.HasProjectDirectory);
        Assert.True(dashboard.OpenProjectDirectoryCommand.CanExecute(null));

        dashboard.OpenProjectDirectoryCommand.Execute(null);
        Assert.Equal(@"C:\code\NightShift", Assert.Single(harness.Shell.Opened));
    }
}

/// <summary>
/// Makes <see cref="Progress{T}"/> synchronous for the duration of a test.
/// </summary>
/// <remarks>
/// <see cref="NightShift.Core.Scheduling.PilotScheduler"/> reports run progress through
/// <see cref="Progress{T}"/>, which without a synchronization context posts to the thread pool. That
/// would make every output-pane assertion a race. Installing an inline context turns the report into
/// a direct call, which is exactly how it behaves under Avalonia's dispatcher anyway.
/// </remarks>
sealed class InlineSynchronizationContextScope : IDisposable
{
    readonly SynchronizationContext? _previous = SynchronizationContext.Current;

    public InlineSynchronizationContextScope() =>
        SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());

    public void Dispose() => SynchronizationContext.SetSynchronizationContext(_previous);

    sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);

        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }
}
