using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NightShift.Core.Configuration;
using NightShift.Core.Execution;
using NightShift.Core.History;
using NightShift.Core.Preflight;
using NightShift.Core.Scheduling;
using NightShift.Core.Usage;
using NightShift.Desktop.Services;

namespace NightShift.Desktop.ViewModels;

/// <summary>
/// The default view (plan.md §9.1): two usage gauges, the project card, the run buttons, the
/// preflight strip and the live output pane.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading.</b> <see cref="PilotScheduler.CycleCompleted"/> and
/// <see cref="PilotScheduler.RunProgress"/> are raised on the background service's thread, and
/// <see cref="ISettingsStore.SettingsChanged"/> on whatever thread saved. Every handler here
/// therefore hops through <see cref="IUiDispatcher"/> before touching bound state.
/// </para>
/// <para>
/// <b>Lifetime.</b> Construct, then <see cref="InitializeAsync"/> once, then
/// <see cref="Tick"/> about once a second (<see cref="MainWindowViewModel"/> owns that timer).
/// <see cref="Dispose"/> detaches the three event handlers; without it the view model outlives the
/// window and keeps the scheduler alive through its delegate.
/// </para>
/// </remarks>
public sealed partial class DashboardViewModel : ViewModelBase, IDisposable
{
    /// <summary>Caption of the 5-hour gauge.</summary>
    public const string SessionGaugeCaption = "Session (5h)";

    /// <summary>Caption of the 7-day gauge.</summary>
    public const string WeeklyGaugeCaption = "Weekly (7d)";

    /// <summary>Body of the confirmation that guards <see cref="ForceRunCommand"/> (plan.md §6.1).</summary>
    public const string ForceRunConfirmationMessage =
        "Force run ignores the usage threshold and starts Claude even when the quota is nearly spent. " +
        "It is the one action that can burn through a weekly cap. Continue?";

    /// <summary>What to tell the user when preflight reports the login is missing or expired.</summary>
    public const string LoginInstructions =
        "NightShift cannot log in for you. Open a terminal, run `claude`, and complete the browser " +
        "sign-in; then re-run preflight here.";

    /// <summary>
    /// Output notifications are coalesced to at most one per this interval. A run emits thousands of
    /// text deltas, and re-materialising a 5000-line string per fragment would peg the UI thread.
    /// </summary>
    public static readonly TimeSpan OutputNotifyInterval = TimeSpan.FromMilliseconds(100);

    readonly PilotScheduler _scheduler;
    readonly ISettingsStore _settings;
    readonly CompositeUsageProvider _usage;
    readonly IPreflightChecker _preflight;
    readonly IPreflightFixApplier _fixApplier;
    readonly RunHistoryStore _history;
    readonly AppPaths _paths;
    readonly IUiDispatcher _dispatcher;
    readonly IShellLauncher _shell;
    readonly IClipboard _clipboard;
    readonly IConfirmationService _confirmation;
    readonly IFolderPicker _folderPicker;
    readonly IFilePicker _filePicker;
    readonly ILogger<DashboardViewModel> _logger;
    readonly TimeProvider _time;
    readonly OutputPaneWriter _output;

    readonly EventHandler<CycleCompleted> _onCycleCompleted;
    readonly EventHandler<RunProgress> _onRunProgress;
    readonly EventHandler<PilotSettings> _onSettingsChanged;

    CancellationTokenSource? _manualRunCts;
    DateTimeOffset _lastOutputNotify = DateTimeOffset.MinValue;
    bool _outputDirty;

    /// <summary>
    /// The newest snapshot, kept so the gate line can be re-rendered when the *metric* changes
    /// without a fresh usage lookup.
    /// </summary>
    UsageSnapshot? _lastSnapshot;
    bool _disposed;

    public DashboardViewModel(
        PilotScheduler scheduler,
        ISettingsStore settings,
        CompositeUsageProvider usage,
        IPreflightChecker preflight,
        IPreflightFixApplier fixApplier,
        RunHistoryStore history,
        AppPaths paths,
        IUiDispatcher dispatcher,
        IShellLauncher shell,
        IClipboard clipboard,
        IConfirmationService confirmation,
        IFolderPicker folderPicker,
        IFilePicker filePicker,
        ILogger<DashboardViewModel> logger,
        TimeProvider? timeProvider = null,
        int outputCapacityLines = OutputRingBuffer.DefaultCapacity)
    : base(dispatcher)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _usage = usage ?? throw new ArgumentNullException(nameof(usage));
        _preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
        _fixApplier = fixApplier ?? throw new ArgumentNullException(nameof(fixApplier));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        _folderPicker = folderPicker ?? throw new ArgumentNullException(nameof(folderPicker));
        _filePicker = filePicker ?? throw new ArgumentNullException(nameof(filePicker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = timeProvider ?? TimeProvider.System;
        _output = new OutputPaneWriter(new OutputRingBuffer(outputCapacityLines));

        SessionGauge = new UsageGaugeViewModel(SessionGaugeCaption);
        WeeklyGauge = new UsageGaugeViewModel(WeeklyGaugeCaption);

        _onCycleCompleted = (_, cycle) => _dispatcher.Post(() => OnCycleCompleted(cycle));
        _onRunProgress = (_, progress) => _dispatcher.Post(() => OnRunProgress(progress));
        _onSettingsChanged = (_, updated) => _dispatcher.Post(() => ApplySettings(updated));

        _scheduler.CycleCompleted += _onCycleCompleted;
        _scheduler.RunProgress += _onRunProgress;
        _settings.SettingsChanged += _onSettingsChanged;

        ApplySettings(_settings.Current);
        IsPaused = !_scheduler.IsEnabled;
        IsRunning = _scheduler.IsRunning;
    }

    // ── Usage gauges (plan.md §9.1) ────────────────────────────────────────────────────────────

    /// <summary>The 5-hour session gauge.</summary>
    public UsageGaugeViewModel SessionGauge { get; }

    /// <summary>The 7-day weekly gauge.</summary>
    public UsageGaugeViewModel WeeklyGauge { get; }

    /// <summary>
    /// The configured skip threshold, so the view can colour the gauges green / amber / red
    /// (plan.md §9.1: green &lt; 70, amber 70–&lt;threshold, red ≥ threshold).
    /// </summary>
    [ObservableProperty]
    public partial double ThresholdPercent { get; private set; } = 90;

    /// <summary>Which window the threshold is compared against, for the gauge captions' emphasis.</summary>
    [ObservableProperty]
    public partial UsageMetric UsageMetric { get; private set; }

    /// <summary>Which provider produced the last snapshot, for the "approximate" explanation.</summary>
    [ObservableProperty]
    public partial UsageSource UsageSource { get; private set; } = UsageSource.None;

    /// <summary>When usage was last read. Null before the first lookup.</summary>
    [ObservableProperty]
    public partial DateTimeOffset? UsageRetrievedAt { get; private set; }

    /// <summary>
    /// The gate figure itself, named: <c>"Highest of all — 41% (7d Opus)"</c>.
    /// </summary>
    /// <remarks>
    /// The dashboard used to show only the metric's *name*, and only two of the four windows
    /// <see cref="UsageMetric.HighestOfAll"/> maximises over have a gauge. A gate driven by the Opus
    /// or Sonnet window therefore moved nothing on screen, which reads as a value that never
    /// refreshes.
    /// </remarks>
    [ObservableProperty]
    public partial string GateMetricText { get; private set; } = string.Empty;

    /// <summary>
    /// The per-model windows that have no gauge: <c>"7d Opus 41% · 7d Sonnet 12%"</c>. Empty when
    /// the provider reported neither, which is always the case under the ccusage fallback.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOtherWindows))]
    public partial string OtherWindowsText { get; private set; } = string.Empty;

    public bool HasOtherWindows => OtherWindowsText.Length > 0;

    /// <summary>
    /// How old the displayed figures are: <c>"Updated 2 min ago"</c>. Refreshed on the one-second
    /// tick, so a stale reading is visibly stale rather than indistinguishable from a steady one.
    /// </summary>
    [ObservableProperty]
    public partial string UsageAgeText { get; private set; } = string.Empty;

    /// <summary>One line per usage provider: whether its last attempt worked, and why not.</summary>
    public ObservableCollection<string> UsageProviderHealth { get; } = [];

    // ── Project card (plan.md §9.1) ────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProjectDirectory))]
    public partial string ProjectDirectory { get; private set; } = string.Empty;

    public bool HasProjectDirectory => ProjectDirectory.Length > 0;

    [ObservableProperty]
    public partial string PlanFileName { get; private set; } = "plan.md";

    /// <summary>plan.md §9.1's "12 of 30 items complete", or why it could not be counted.</summary>
    [ObservableProperty]
    public partial string PlanItemsSummary { get; private set; } = "No plan file has been read yet.";

    [ObservableProperty]
    public partial int PlanCompletedCount { get; private set; }

    [ObservableProperty]
    public partial int PlanRemainingCount { get; private set; }

    /// <summary>Items a previous run marked <c>- [!]</c> because it hit a wall (plan.md §5.2).</summary>
    [ObservableProperty]
    public partial int PlanBlockedCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlanItems))]
    public partial int PlanTotalCount { get; private set; }

    public bool HasPlanItems => PlanTotalCount > 0;

    /// <summary>Completed / total, 0–1, for a progress bar. Zero when nothing has been counted.</summary>
    [ObservableProperty]
    public partial double PlanProgressFraction { get; private set; }

    /// <summary>The last run that actually launched Claude, ignoring skips. Null on a fresh install.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastRun))]
    public partial RunRecord? LastRun { get; private set; }

    public bool HasLastRun => LastRun is not null;

    /// <summary>Outcome badge text for the project card, e.g. <c>Success</c> or <c>Rate limited</c>.</summary>
    [ObservableProperty]
    public partial string LastRunOutcomeText { get; private set; } = "No runs yet";

    /// <summary>When the last run started, already worded, e.g. <c>3h 12m ago</c>.</summary>
    [ObservableProperty]
    public partial string LastRunStartedText { get; private set; } = string.Empty;

    /// <summary>The last run's truncated final assistant text.</summary>
    [ObservableProperty]
    public partial string LastRunSummary { get; private set; } = string.Empty;

    // ── Run state and the mid-run quota block (plan.md §6.1) ───────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    public partial bool IsPaused { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunNowCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceRunCommand))]
    public partial bool IsRunning { get; private set; }

    /// <summary>How long the in-flight run has been going, e.g. <c>3m</c>. Empty when idle.</summary>
    [ObservableProperty]
    public partial string RunElapsedText { get; private set; } = string.Empty;

    /// <summary>When the current run started, for <see cref="RunElapsedText"/>.</summary>
    [ObservableProperty]
    public partial DateTimeOffset? RunStartedAt { get; private set; }

    /// <summary>
    /// True when the last run was cut short by the subscription quota and the window has not rolled
    /// over yet (plan.md §6.1). Nothing is broken — the pilot is waiting, and will resume the same
    /// session rather than restart it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RateLimitMessage))]
    public partial bool IsRateLimited { get; private set; }

    /// <summary>When the exhausted window rolls over. Null when the CLI reported no reset time.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RateLimitMessage))]
    public partial DateTimeOffset? RateLimitResetsAt { get; private set; }

    /// <summary>The session that will be resumed when quota returns, if one is pending.</summary>
    [ObservableProperty]
    public partial string? PendingResumeSessionId { get; private set; }

    /// <summary>Banner text for the quota block. Empty when <see cref="IsRateLimited"/> is false.</summary>
    public string RateLimitMessage
    {
        get
        {
            if (!IsRateLimited)
            {
                return string.Empty;
            }

            var resume = PendingResumeSessionId is { Length: > 0 }
                ? " The interrupted session will be resumed, not restarted."
                : string.Empty;

            return RateLimitResetsAt is { } resetsAt
                ? $"Quota ran out mid-run. Waiting until {resetsAt.ToLocalTime():t} " +
                  $"({TimeSpanText.DescribeUntil(resetsAt, _time.GetUtcNow())}).{resume}"
                : $"Quota ran out mid-run and no reset time was reported; the pilot falls back to the interval.{resume}";
        }
    }

    /// <summary>The most recent thing a command did, for a status line under the buttons.</summary>
    [ObservableProperty]
    public partial string StatusMessage { get; private set; } = string.Empty;

    // ── Preflight strip (plan.md §9.1) ─────────────────────────────────────────────────────────

    /// <summary>The pills, rebuilt from each preflight result in plan.md §7 table order.</summary>
    public ObservableCollection<PreflightCheckViewModel> PreflightChecks { get; } = [];

    /// <summary>The worst status present — the colour of the summary pill.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreflightErrors))]
    public partial PreflightStatus PreflightStatus { get; private set; } = PreflightStatus.Ok;

    public bool HasPreflightErrors => PreflightStatus == PreflightStatus.Error;

    /// <summary>One line naming the blockers, from <see cref="PreflightResult.Summary"/>.</summary>
    [ObservableProperty]
    public partial string PreflightSummary { get; private set; } = "Preflight has not run yet.";

    [ObservableProperty]
    public partial DateTimeOffset? PreflightCheckedAt { get; private set; }

    /// <summary>What the last fix attempt did. Empty until one has been attempted.</summary>
    [ObservableProperty]
    public partial string LastFixMessage { get; private set; } = string.Empty;

    /// <summary>
    /// Set when a preflight fix is the <see cref="PreflightFixCommand.ShowLoginInstructions"/> one:
    /// Core deliberately never touches credentials, so the app just explains what to type.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoginInstructionsVisible))]
    public partial string LoginInstructionsMessage { get; private set; } = string.Empty;

    public bool IsLoginInstructionsVisible => LoginInstructionsMessage.Length > 0;

    // ── Live output pane (plan.md §9.1) ────────────────────────────────────────────────────────

    /// <summary>
    /// Everything currently in the bounded buffer. Change notifications are coalesced to one per
    /// <see cref="OutputNotifyInterval"/>; call <see cref="FlushOutput"/> to force one.
    /// </summary>
    public string OutputText => _output.Buffer.GetText();

    /// <summary>Lines held in memory. Never exceeds <see cref="OutputCapacityLines"/>.</summary>
    public int OutputLineCount => _output.Buffer.LineCount;

    /// <summary>Lines dropped off the front of the ring buffer during this run.</summary>
    public int OutputDroppedLineCount => _output.Buffer.DroppedLineCount;

    /// <summary>The cap, so the view can say "showing the last 5,000 lines".</summary>
    public int OutputCapacityLines => _output.Buffer.Capacity;

    /// <summary>True once the buffer has started dropping lines; the full text is still on disk.</summary>
    public bool IsOutputTruncated => _output.Buffer.DroppedLineCount > 0;

    /// <summary>Whether the pane should follow the tail. Two-way; the view toggles it on scroll.</summary>
    [ObservableProperty]
    public partial bool AutoScrollOutput { get; set; } = true;

    /// <summary>The run whose events are currently filling the pane.</summary>
    [ObservableProperty]
    public partial string? CurrentRunId { get; private set; }

    /// <summary>
    /// The on-disk transcript for <see cref="CurrentRunId"/> — what <see cref="OpenLogCommand"/>
    /// opens, and the reason the in-memory buffer is allowed to be lossy.
    /// </summary>
    [ObservableProperty]
    public partial string? CurrentTranscriptPath { get; private set; }

    // ── Lifecycle ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// First load: settings, the last run, preflight and usage. Safe to call once; it never throws,
    /// because a dashboard that cannot render is worse than one showing stale figures.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ApplySettings(_settings.Current);
        await LoadLastRunAsync(cancellationToken).ConfigureAwait(false);

        if (_scheduler.LastPreflight is { } existing)
        {
            ApplyPreflight(existing);
        }
        else
        {
            await RunPreflightAsync(cancellationToken).ConfigureAwait(false);
        }

        await RefreshUsageAsync(cancellationToken).ConfigureAwait(false);
        Tick();
    }

    /// <summary>
    /// Re-renders everything that is a function of "now": the reset countdowns, the elapsed-run
    /// text and the quota-block banner. Driven by <see cref="MainWindowViewModel"/>'s timer so the
    /// app has exactly one clock.
    /// </summary>
    public void Tick()
    {
        var now = _time.GetUtcNow();

        SessionGauge.RefreshSubCaption(now);
        WeeklyGauge.RefreshSubCaption(now);
        RefreshUsageAge(now);

        IsRunning = _scheduler.IsRunning;
        IsPaused = !_scheduler.IsEnabled;

        RunElapsedText = IsRunning && RunStartedAt is { } startedAt
            ? TimeSpanText.DescribeElapsed(now - startedAt)
            : string.Empty;

        if (IsRateLimited && RateLimitResetsAt is { } resetsAt && resetsAt <= now)
        {
            IsRateLimited = false;
            RateLimitResetsAt = null;
        }

        OnPropertyChanged(nameof(RateLimitMessage));
        LastRunStartedText = LastRun is { } run ? TimeSpanText.Describe(now - run.StartedAt) + " ago" : string.Empty;

        FlushOutput();
    }

    /// <summary>
    /// Raises the coalesced output notifications immediately. Called on every tick, and by tests
    /// that need to observe <see cref="OutputText"/> without advancing a clock.
    /// </summary>
    public void FlushOutput()
    {
        if (_outputDirty)
        {
            NotifyOutputChanged(force: true);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _scheduler.CycleCompleted -= _onCycleCompleted;
        _scheduler.RunProgress -= _onRunProgress;
        _settings.SettingsChanged -= _onSettingsChanged;
        _manualRunCts?.Dispose();
        _manualRunCts = null;
    }

    // ── Commands (plan.md §9.1 "Buttons" and the output-pane toolbar) ──────────────────────────

    /// <summary>
    /// plan.md §6.1's <c>RunNowCommand</c>: bypasses the interval but still honours the usage check.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartRun))]
    async Task RunNowAsync()
    {
        await StartRunAsync(bypassUsageCheck: false).ConfigureAwait(false);
    }

    /// <summary>
    /// plan.md §6.1's <c>ForceRunCommand</c>: bypasses the usage check, behind a confirmation. The
    /// dialog is not optional — this is the only action that can burn a weekly cap on purpose.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartRun))]
    async Task ForceRunAsync()
    {
        var confirmed = await _confirmation
            .ConfirmAsync("Force run", ForceRunConfirmationMessage, "Force run")
            .ConfigureAwait(false);

        if (!confirmed)
        {
            StatusMessage = "Force run cancelled.";
            return;
        }

        await StartRunAsync(bypassUsageCheck: true).ConfigureAwait(false);
    }

    bool CanStartRun => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanPause))]
    void Pause()
    {
        _scheduler.Pause();
        IsPaused = !_scheduler.IsEnabled;
        StatusMessage = "Paused. Scheduled cycles will be skipped until you resume.";
    }

    bool CanPause => !IsPaused;

    [RelayCommand(CanExecute = nameof(CanResume))]
    void Resume()
    {
        _scheduler.Resume();
        IsPaused = !_scheduler.IsEnabled;
        StatusMessage = "Resumed.";
    }

    bool CanResume => IsPaused;

    [RelayCommand]
    async Task RefreshUsageAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Forced: the button exists precisely because the figures on screen look stale, and
            // returning the same cached snapshot would confirm the suspicion rather than answer it.
            var snapshot = await _usage
                .GetUsageAsync(forceRefresh: true, cancellationToken)
                .ConfigureAwait(false);
            ApplyUsage(snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Refreshing usage failed.");
            ApplyUsage(UsageSnapshot.Unavailable(ex.Message, _time.GetUtcNow()));
        }
    }

    [RelayCommand]
    async Task RunPreflightAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _preflight.CheckAsync(_settings.Current, cancellationToken).ConfigureAwait(false);
            ApplyPreflight(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Preflight threw while refreshing the dashboard.");
            PreflightSummary = $"Preflight could not be evaluated: {ex.Message}";
            PreflightStatus = PreflightStatus.Error;
        }
    }

    /// <summary>
    /// Stops whichever run is in flight, whoever started it (plan.md §9.1).
    /// </summary>
    /// <remarks>
    /// A manual run is cancelled through the token this view model holds. A run the background
    /// scheduler owns has no such token, so it goes through
    /// <see cref="PilotScheduler.StopCurrentRun"/>, which cancels the cycle's own linked source.
    /// Both paths end with the runner killing the process tree and recording the run, so a stopped
    /// run is history rather than an unexplained gap.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanStopRun))]
    void StopRun()
    {
        StatusMessage = "Stopping the run…";

        var cts = _manualRunCts;
        if (cts is not null)
        {
            try
            {
                cts.Cancel();
                return;
            }
            catch (ObjectDisposedException)
            {
                // The run finished between the CanExecute check and here; fall through to the
                // scheduler, which will simply report that nothing was in flight.
            }
        }

        if (!_scheduler.StopCurrentRun())
        {
            StatusMessage = "Nothing is running.";
        }
    }

    /// <summary>Enabled whenever anything is running, not only a run this window started.</summary>
    bool CanStopRun => _manualRunCts is not null || _scheduler.IsRunning;

    [RelayCommand]
    async Task CopyOutputAsync(CancellationToken cancellationToken)
    {
        var copied = await _clipboard.TrySetTextAsync(OutputText, cancellationToken).ConfigureAwait(false);
        StatusMessage = copied ? "Output copied to the clipboard." : "Could not copy to the clipboard.";
    }

    /// <summary>Opens the current run's transcript, falling back to the logs directory.</summary>
    [RelayCommand]
    void OpenLog()
    {
        var target = CurrentTranscriptPath is { Length: > 0 } transcript && File.Exists(transcript)
            ? transcript
            : LastRun is { } run && File.Exists(_history.TranscriptPath(run.Id))
                ? _history.TranscriptPath(run.Id)
                : _paths.LogsDirectory;

        if (!_shell.OpenPath(target))
        {
            StatusMessage = $"Could not open {target}.";
        }
    }

    [RelayCommand]
    void ClearOutput()
    {
        _output.Buffer.Clear();
        _output.Reset();
        NotifyOutputChanged(force: true);
    }

    [RelayCommand(CanExecute = nameof(HasProjectDirectory))]
    void OpenProjectDirectory()
    {
        if (!_shell.OpenPath(ProjectDirectory))
        {
            StatusMessage = $"Could not open {ProjectDirectory}.";
        }
    }

    [RelayCommand]
    void DismissLoginInstructions() => LoginInstructionsMessage = string.Empty;

    // ── Preflight fix dispatch (plan.md §7) ────────────────────────────────────────────────────

    /// <summary>
    /// Routes a pill's fix. <see cref="PreflightFixAction.IsAppliedByCore"/> ones go straight to
    /// <see cref="IPreflightFixApplier"/>; the rest are the app's — a picker, the shell, or an
    /// explanation — because Core has no UI and must never pretend to have one.
    /// </summary>
    async Task ApplyFixAsync(PreflightCheckViewModel check, CancellationToken cancellationToken)
    {
        if (check.Fix is not { } fix)
        {
            return;
        }

        var settings = _settings.Current;

        if (fix.IsAppliedByCore)
        {
            var result = await _fixApplier.ApplyAsync(fix, settings, cancellationToken).ConfigureAwait(false);
            LastFixMessage = result.Message;

            if (result.Succeeded)
            {
                await RunPreflightAsync(cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        switch (fix.Command)
        {
            case PreflightFixCommand.SetProjectDirectory:
            {
                var picked = await _folderPicker
                    .PickFolderAsync("Choose the project directory", fix.Target, cancellationToken)
                    .ConfigureAwait(false);

                if (picked is { Length: > 0 })
                {
                    await SaveAsync(settings with { ProjectDirectory = picked }, cancellationToken)
                        .ConfigureAwait(false);
                    LastFixMessage = $"Project directory set to {picked}.";
                    await RunPreflightAsync(cancellationToken).ConfigureAwait(false);
                }

                break;
            }

            case PreflightFixCommand.SetClaudeExecutablePath:
            {
                var picked = await _filePicker
                    .PickFileAsync("Locate the Claude Code executable", fix.Target, null, cancellationToken)
                    .ConfigureAwait(false);

                if (picked is { Length: > 0 })
                {
                    await SaveAsync(settings with { ClaudeExecutablePath = picked }, cancellationToken)
                        .ConfigureAwait(false);
                    LastFixMessage = $"Claude Code path set to {picked}.";
                    await RunPreflightAsync(cancellationToken).ConfigureAwait(false);
                }

                break;
            }

            case PreflightFixCommand.OpenPlanFile:
            case PreflightFixCommand.OpenSettingsFile:
            {
                var target = fix.Target;
                LastFixMessage = target is { Length: > 0 } && _shell.OpenPath(target)
                    ? $"Opened {target}."
                    : $"Could not open {target ?? "the file"}.";
                break;
            }

            case PreflightFixCommand.ShowLoginInstructions:
                LoginInstructionsMessage = LoginInstructions;
                LastFixMessage = LoginInstructions;
                break;

            default:
                LastFixMessage = $"`{fix.Command}` has no handler in the app.";
                _logger.LogWarning("Unhandled app-side preflight fix {Command}.", fix.Command);
                break;
        }
    }

    // ── Event handlers, already marshalled onto the UI thread ──────────────────────────────────

    void OnCycleCompleted(CycleCompleted cycle)
    {
        // Not `_scheduler.IsRunning`: the scheduler raises this while it still holds the run gate,
        // so the gate reads "busy" for a cycle that is, by the name of this event, over.
        IsRunning = false;
        RunStartedAt = null;
        RunElapsedText = string.Empty;
        IsPaused = !_scheduler.IsEnabled;

        if (_scheduler.LastPreflight is { } preflight)
        {
            ApplyPreflight(preflight);
        }

        // Post-run figure first. `Decision.Usage` is what the gate decided on *before* Claude spent
        // anything, so preferring it would leave the gauges showing a pre-run reading for the whole
        // interval — the "it never refreshes" symptom.
        if ((cycle.UsageAfterRun ?? cycle.Decision.Usage) is { } usage)
        {
            ApplyUsage(usage);
        }

        if (cycle.Record is { } record)
        {
            if (record.Outcome != RunOutcome.Skipped)
            {
                ApplyLastRun(record);
            }

            ApplyRateLimit(record);
        }

        StatusMessage = cycle.Decision.Detail;
    }

    void OnRunProgress(RunProgress progress)
    {
        if (CurrentRunId != progress.RunId)
        {
            CurrentRunId = progress.RunId;
            CurrentTranscriptPath = _history.TranscriptPath(progress.RunId);
            RunStartedAt = _time.GetUtcNow();
            IsRunning = true;
            _output.Buffer.Clear();
            _output.Reset();
            NotifyOutputChanged(force: true);
        }

        if (progress.Event is ClaudeRateLimitEvent rateLimit)
        {
            ApplyLiveRateLimit(rateLimit);
        }

        if (_output.Write(progress.Event))
        {
            NotifyOutputChanged(force: false);
        }
    }

    /// <summary>
    /// Folds a mid-run <c>rate_limit_event</c> into the displayed snapshot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The CLI volunteers the quota it just saw, free and mid-run, and a run is exactly when the
    /// figures on screen go stale fastest. This is <b>display only</b>: the gate is decided in
    /// <c>CycleDecisionMaker</c> from a snapshot the scheduler fetched itself, and nothing here
    /// reaches it.
    /// </para>
    /// <para>
    /// Uses <see cref="ClaudeRateLimitEvent.UtilizationPercent"/>, never
    /// <see cref="ClaudeRateLimitEvent.UtilizationFraction"/> — the wire value is a fraction 0–1 and
    /// confusing the two by 100× in an app built around a percentage threshold is the documented
    /// trap.
    /// </para>
    /// </remarks>
    void ApplyLiveRateLimit(ClaudeRateLimitEvent rateLimit)
    {
        if (_lastSnapshot is not { IsAvailable: true } snapshot || rateLimit.UtilizationPercent is not { } percent)
        {
            return;
        }

        var window = new UsageWindow(percent, rateLimit.ResetsAt);

        // The wire names are Claude Code's, not ours; anything else is a window we have no gauge for
        // and no safe place to put.
        var updated = rateLimit.RateLimitType switch
        {
            "five_hour" => snapshot with { FiveHour = window },
            "seven_day" => snapshot with { SevenDay = window },
            "seven_day_opus" => snapshot with { SevenDayOpus = window },
            "seven_day_sonnet" => snapshot with { SevenDaySonnet = window },
            _ => null,
        };

        if (updated is null)
        {
            return;
        }

        ApplyUsage(updated with { RetrievedAt = _time.GetUtcNow() });
    }

    // ── Projection helpers ─────────────────────────────────────────────────────────────────────

    void ApplySettings(PilotSettings settings)
    {
        var normalized = settings.Normalized();

        // NotifyCanExecuteChanged raises CanExecuteChanged synchronously, and Avalonia throws when a
        // bound command does that off the UI thread — which is exactly where this lands when it is
        // reached from an awaited continuation.
        OnUiThread(() =>
        {
            ProjectDirectory = normalized.ProjectDirectory;
            PlanFileName = normalized.PlanFileName;
            ThresholdPercent = normalized.ThresholdPercent;
            UsageMetric = normalized.UsageMetric;

            // The gate line names the winning window, so switching metric changes it even though no
            // new usage figure arrived.
            ApplyGateMetric(_lastSnapshot);

            OpenProjectDirectoryCommand.NotifyCanExecuteChanged();
        });
    }

    void ApplyUsage(UsageSnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        UsageSource = snapshot.Source;
        UsageRetrievedAt = snapshot.RetrievedAt;

        var now = _time.GetUtcNow();

        if (!snapshot.IsAvailable)
        {
            SessionGauge.SetUnavailable(snapshot.UnavailableReason);
            WeeklyGauge.SetUnavailable(snapshot.UnavailableReason);
        }
        else
        {
            SessionGauge.Update(snapshot.FiveHour, snapshot.IsApproximate, snapshot.UnavailableReason, now);
            WeeklyGauge.Update(snapshot.SevenDay, snapshot.IsApproximate, snapshot.UnavailableReason, now);
        }

        ApplyGateMetric(snapshot);
        RefreshUsageAge(now);

        UsageProviderHealth.Clear();
        foreach (var health in _usage.Health)
        {
            UsageProviderHealth.Add(health.IsHealthy
                ? $"{health.Source}: healthy"
                : $"{health.Source}: {health.LastReason ?? "unavailable"}");
        }
    }

    /// <summary>
    /// Renders the gate line and the no-gauge windows from the newest snapshot. Called on every
    /// usage update and again whenever the metric setting changes, so the line always describes the
    /// comparison the scheduler would make right now.
    /// </summary>
    void ApplyGateMetric(UsageSnapshot? snapshot)
    {
        var label = UsageMetricText.Describe(UsageMetric);

        if (snapshot is null)
        {
            GateMetricText = label;
            OtherWindowsText = string.Empty;
            return;
        }

        var reading = snapshot.SelectDetailed(UsageMetric);

        // Unknown is spelled out rather than left blank: a blank gate figure beside two drawn gauges
        // reads as 0%, which is the exact misreading the usage gate exists to prevent (plan.md §4.3).
        GateMetricText = reading is { Percent: { } percent, WindowName: { } window }
            ? $"{label} — {percent:0.##}% ({window})"
            : $"{label} — unknown";

        // Only the two per-model windows: the 5h and 7d ones already have gauges above this line.
        var others = UsageMetricSelector.NamedWindows(snapshot)
            .Where(pair => pair.Window is not null
                && pair.Name is UsageMetricSelector.SevenDayOpusName or UsageMetricSelector.SevenDaySonnetName)
            .Select(pair => $"{pair.Name} {pair.Window!.UtilizationPercent:0.##}%");

        OtherWindowsText = string.Join(" · ", others);
    }

    /// <summary>
    /// "Updated 2 min ago". Recomputed on the tick rather than only on refresh, because the whole
    /// point of the line is to make an *unchanging* figure visibly age.
    /// </summary>
    void RefreshUsageAge(DateTimeOffset now)
    {
        if (UsageRetrievedAt is not { } retrievedAt)
        {
            UsageAgeText = string.Empty;
            return;
        }

        var age = now - retrievedAt;
        UsageAgeText = age < TimeSpan.FromSeconds(10)
            ? "Updated just now"
            : $"Updated {TimeSpanText.Describe(age)} ago";
    }

    void ApplyPreflight(PreflightResult result)
    {
        // Rebuilding a bound ObservableCollection off the UI thread corrupts the ItemsControl:
        // Avalonia posts the notifications, so Clear()'s Reset is applied after the collection has
        // already been refilled and the queued Adds are replayed — twelve pills became twenty-four.
        OnUiThread(() =>
        {
            PreflightChecks.Clear();
            foreach (var check in result.Checks)
            {
                PreflightChecks.Add(new PreflightCheckViewModel(check, ApplyFixAsync));
            }
        });

        PreflightStatus = result.Status;
        PreflightSummary = result.Summary;
        PreflightCheckedAt = result.CheckedAt;

        if (result.PlanItems is { } counts)
        {
            PlanCompletedCount = counts.Completed;
            PlanRemainingCount = counts.Remaining;
            PlanBlockedCount = counts.Blocked;
            PlanTotalCount = counts.Total;
            PlanProgressFraction = counts.Total == 0 ? 0 : (double)counts.Completed / counts.Total;
            PlanItemsSummary = counts.Summary;
        }
        else
        {
            PlanCompletedCount = 0;
            PlanRemainingCount = 0;
            PlanBlockedCount = 0;
            PlanTotalCount = 0;
            PlanProgressFraction = 0;
            PlanItemsSummary = $"{PlanFileName} could not be read.";
        }
    }

    void ApplyLastRun(RunRecord record)
    {
        LastRun = record;
        LastRunOutcomeText = DescribeOutcome(record.Outcome);
        LastRunSummary = record.Summary ?? string.Empty;
        LastRunStartedText = TimeSpanText.Describe(_time.GetUtcNow() - record.StartedAt) + " ago";
    }

    void ApplyRateLimit(RunRecord record)
    {
        if (record.Outcome != RunOutcome.RateLimited)
        {
            if (record.Outcome is not RunOutcome.Skipped)
            {
                IsRateLimited = false;
                RateLimitResetsAt = null;
                PendingResumeSessionId = null;
            }

            return;
        }

        IsRateLimited = record.RateLimitResetsAt is null || record.RateLimitResetsAt > _time.GetUtcNow();
        RateLimitResetsAt = record.RateLimitResetsAt;
        PendingResumeSessionId = record.IsResumable ? record.SessionId : null;
        OnPropertyChanged(nameof(RateLimitMessage));
    }

    async Task LoadLastRunAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<RunRecord> records;
        try
        {
            records = await _history.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read the run history for the project card.");
            return;
        }

        // Skips are recorded too, and a quiet night produces one per interval; the card is about the
        // last time the pilot actually did something.
        var last = records.LastOrDefault(record => record.Outcome != RunOutcome.Skipped);
        if (last is null)
        {
            return;
        }

        ApplyLastRun(last);
        ApplyRateLimit(last);
    }

    async Task StartRunAsync(bool bypassUsageCheck)
    {
        var cts = new CancellationTokenSource();
        _manualRunCts = cts;
        OnUiThread(StopRunCommand.NotifyCanExecuteChanged);

        IsRunning = true;
        RunStartedAt = _time.GetUtcNow();
        StatusMessage = bypassUsageCheck ? "Force run started." : "Run started.";

        try
        {
            var cycle = await _scheduler.RunNowAsync(bypassUsageCheck, cts.Token).ConfigureAwait(false);
            StatusMessage = cycle.Decision.Detail;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Run stopped.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A manual run failed.");
            StatusMessage = $"Run failed: {ex.Message}";
        }
        finally
        {
            _manualRunCts = null;
            cts.Dispose();

            // This finally runs on a thread-pool thread the moment a manual run ends, so the
            // command notification must be marshalled or it takes the app down.
            OnUiThread(() =>
            {
                StopRunCommand.NotifyCanExecuteChanged();
                IsRunning = _scheduler.IsRunning;
                RunStartedAt = null;
                RunElapsedText = string.Empty;
            });
        }
    }

    async Task SaveAsync(PilotSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            await _settings.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save settings from a preflight fix.");
            LastFixMessage = $"Could not save settings: {ex.Message}";
        }
    }

    void NotifyOutputChanged(bool force)
    {
        var now = _time.GetUtcNow();
        if (!force && now - _lastOutputNotify < OutputNotifyInterval)
        {
            _outputDirty = true;
            return;
        }

        _lastOutputNotify = now;
        _outputDirty = false;
        OnPropertyChanged(nameof(OutputText));
        OnPropertyChanged(nameof(OutputLineCount));
        OnPropertyChanged(nameof(OutputDroppedLineCount));
        OnPropertyChanged(nameof(IsOutputTruncated));
    }

    /// <summary>Badge wording for a run outcome. <c>RateLimited</c> reads as two words.</summary>
    public static string DescribeOutcome(RunOutcome outcome) => outcome switch
    {
        RunOutcome.Success => "Success",
        RunOutcome.Failed => "Failed",
        RunOutcome.TimedOut => "Timed out",
        RunOutcome.Skipped => "Skipped",
        RunOutcome.Launched => "Launched",
        RunOutcome.RateLimited => "Rate limited",
        _ => outcome.ToString(),
    };
}
