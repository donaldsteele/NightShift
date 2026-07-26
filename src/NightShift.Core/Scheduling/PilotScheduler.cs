using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NightShift.Core.Configuration;
using NightShift.Core.Execution;
using NightShift.Core.History;
using NightShift.Core.Preflight;
using NightShift.Core.Usage;

namespace NightShift.Core.Scheduling;

/// <summary>Why a cycle happened, for the log and the status pill.</summary>
public enum CycleTrigger
{
    Scheduled,
    Startup,

    /// <summary>User pressed "Run now" — bypasses the interval, still honours the usage check.</summary>
    RunNow,

    /// <summary>User pressed "Force run" — bypasses the usage check too.</summary>
    ForceRun,
}

/// <summary>One completed cycle, whether it ran or skipped.</summary>
public sealed record CycleCompleted(CycleTrigger Trigger, CycleDecision Decision, RunRecord? Record);

/// <summary>
/// Wakes on an interval, decides whether to run, and never lets two runs overlap (plan.md §6).
/// </summary>
public sealed class PilotScheduler : BackgroundService
{
    readonly ISettingsStore _settings;
    readonly CompositeUsageProvider _usage;
    readonly IPreflightChecker _preflight;
    readonly RunGate _gate;
    readonly PilotStateStore _stateStore;
    readonly RunHistoryStore _history;
    readonly IEnumerable<IClaudeRunner> _runners;
    readonly ILogger<PilotScheduler> _logger;
    readonly TimeProvider _timeProvider;

    PilotState _state = new();

    /// <summary>
    /// The most recent snapshot, kept so the schedule can stay anchored to quota resets even on
    /// cycles that did not fetch usage (a forced run, or a skip that short-circuited earlier).
    /// Reset times are absolute, so a snapshot stays useful for anchoring long after it was taken.
    /// </summary>
    UsageSnapshot? _lastUsage;

    public PilotScheduler(
        ISettingsStore settings,
        CompositeUsageProvider usage,
        IPreflightChecker preflight,
        RunGate gate,
        PilotStateStore stateStore,
        RunHistoryStore history,
        IEnumerable<IClaudeRunner> runners,
        ILogger<PilotScheduler> logger,
        TimeProvider? timeProvider = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _usage = usage ?? throw new ArgumentNullException(nameof(usage));
        _preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _runners = runners ?? throw new ArgumentNullException(nameof(runners));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>False while paused. A paused pilot still ticks; it just skips every cycle.</summary>
    public bool IsEnabled { get; private set; } = true;

    /// <summary>When the next cycle is due, for the status pill and the tray tooltip.</summary>
    public DateTimeOffset? NextRunAt => _state.NextRunAtUtc;

    /// <summary>True while a run holds the gate.</summary>
    public bool IsRunning => _gate.IsRunning;

    /// <summary>The most recent preflight result, for the dashboard's check pills.</summary>
    public PreflightResult? LastPreflight { get; private set; }

    /// <summary>Raised after every cycle, run or skip.</summary>
    public event EventHandler<CycleCompleted>? CycleCompleted;

    /// <summary>Raised for each stream event of a live run, for the dashboard's output pane.</summary>
    public event EventHandler<RunProgress>? RunProgress;

    public void Pause()
    {
        IsEnabled = false;
        _logger.LogInformation("Pilot paused.");
    }

    public void Resume()
    {
        IsEnabled = true;
        _logger.LogInformation("Pilot resumed.");
    }

    /// <summary>
    /// Runs a cycle immediately. <paramref name="bypassUsageCheck"/> is the "Force run" path and
    /// still respects the overlap guard — nothing may ever start a second concurrent run.
    /// </summary>
    public Task<CycleCompleted> RunNowAsync(bool bypassUsageCheck = false, CancellationToken cancellationToken = default) =>
        ExecuteCycleAsync(
            bypassUsageCheck ? CycleTrigger.ForceRun : CycleTrigger.RunNow,
            cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _state = await _stateStore.LoadAsync(stoppingToken).ConfigureAwait(false);
        IsEnabled = !_state.IsPaused;

        // Hand a session interrupted before the last shutdown back to the runner, so work resumes
        // where it stopped rather than starting the item again.
        if (_state.PendingResumeSessionId is { Length: > 0 } pending)
        {
            foreach (var headless in _runners.OfType<HeadlessClaudeRunner>())
            {
                headless.RestorePendingResume(pending);
            }

            _logger.LogInformation(
                "Session {SessionId} was cut short before shutdown and will be resumed{When}.",
                pending,
                _state.QuotaResumesAtUtc is { } at ? $" after {at:u}" : string.Empty);
        }

        var settings = _settings.Current;
        var now = _timeProvider.GetUtcNow();

        // A missed schedule (the machine was asleep, the app was closed) runs at once when the user
        // asked for that; otherwise the cadence simply resumes.
        // A quota block survives a restart. Starting a cycle into a window we already know is spent
        // would just burn a usage lookup and log a skip.
        var quotaWaitApplied = _state.QuotaResumesAtUtc is { } pendingQuota && pendingQuota > now;

        if (_state.QuotaResumesAtUtc is { } quotaReturnsAt && quotaWaitApplied)
        {
            _state.NextRunAtUtc = quotaReturnsAt + TimeSpan.FromMinutes(settings.QuotaResetGraceMinutes);
            _logger.LogInformation(
                "Quota was exhausted before the last shutdown; the next check waits until {Next:u}.",
                _state.NextRunAtUtc);
            await _stateStore.SaveAsync(_state, stoppingToken).ConfigureAwait(false);
        }
        else if (settings.RunOnStartup || (_state.NextRunAtUtc is { } due && due <= now))
        {
            _logger.LogInformation(
                "Startup cycle: {Reason}.",
                settings.RunOnStartup ? "RunOnStartup is enabled" : $"the schedule was due at {_state.NextRunAtUtc:u}");
            await ExecuteCycleAsync(CycleTrigger.Startup, stoppingToken).ConfigureAwait(false);
        }
        else if (settings.AlignToQuotaReset)
        {
            // Anchor the very first tick too. Without this the schedule only starts tracking resets
            // after a cycle has already run, so the first interval after every restart is blind.
            try
            {
                _lastUsage = await _usage.GetUsageAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Could not read usage to anchor the first tick; using the interval.");
            }
        }

        // Do not overwrite a quota wait that was just restored from state.
        if (!quotaWaitApplied)
        {
            await ScheduleNextAsync(_settings.Current, null, _lastUsage, null, stoppingToken).ConfigureAwait(false);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = NextDelay();
            try
            {
                await Task.Delay(delay, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var cycle = await ExecuteCycleAsync(CycleTrigger.Scheduled, stoppingToken).ConfigureAwait(false);
            await ScheduleNextAsync(
                _settings.Current,
                cycle.Decision.ResumeAt,
                cycle.Decision.Usage ?? _lastUsage,
                cycle.Record,
                stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// How long until the next due time, clamped to at least one second so a past-due schedule
    /// cannot spin.
    /// </summary>
    TimeSpan NextDelay()
    {
        var interval = TimeSpan.FromMinutes(_settings.Current.IntervalMinutes);
        if (_state.NextRunAtUtc is not { } due)
        {
            return interval;
        }

        var remaining = due - _timeProvider.GetUtcNow();
        return remaining < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : remaining;
    }

    /// <summary>
    /// Sets the next due time, anchored to quota resets when we know them (plan.md §6).
    /// </summary>
    /// <remarks>
    /// The interval is an upper bound, not a metronome. A window rolling over is the moment the most
    /// quota becomes available, so the next check is pulled forward to just after the soonest reset
    /// we know about — and when the threshold blocked this cycle, that reset is the first moment the
    /// pilot could run at all. Alignment only ever moves a check <b>earlier</b>: a distant reset
    /// never delays the ordinary cadence.
    /// </remarks>
    /// <param name="blockingResetAt">
    /// The reset of the window that blocked this cycle, if one did. Preferred over the general
    /// anchor because waking before it just produces another skip.
    /// </param>
    /// <param name="usage">The snapshot this cycle saw, for its reset times.</param>
    async Task ScheduleNextAsync(
        PilotSettings settings,
        DateTimeOffset? blockingResetAt,
        UsageSnapshot? usage,
        RunRecord? record,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var grace = TimeSpan.FromMinutes(settings.QuotaResetGraceMinutes);
        var byInterval = now + TimeSpan.FromMinutes(settings.IntervalMinutes);

        // A run cut short by quota is the one case where the next check may be pushed LATER than the
        // interval. Everywhere else alignment only moves checks earlier, but here we have hard
        // evidence from the run itself that the window is spent: ticking hourly against it would
        // just produce a queue of skips, and each attempt still costs a usage lookup.
        if (record is { Outcome: RunOutcome.RateLimited, RateLimitResetsAt: { } quotaReturnsAt }
            && quotaReturnsAt + grace > now)
        {
            _state.NextRunAtUtc = quotaReturnsAt + grace;
            _logger.LogWarning(
                "Run {RunId} was cut short by quota. Waiting until {Next:u}, when the {Window} window rolls over.",
                record.Id,
                _state.NextRunAtUtc,
                record.SkipDetail);
            await _stateStore.SaveAsync(_state, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (record is { Outcome: RunOutcome.RateLimited })
        {
            // Rate limited but Claude never said when it clears. Backing off by a whole interval is
            // better than retrying immediately into a wall.
            _logger.LogWarning(
                "Run {RunId} was cut short by quota but reported no reset time; falling back to the interval.",
                record.Id);
        }

        var next = byInterval;
        string? reason = null;

        if (settings.AlignToQuotaReset)
        {
            // A cycle blocked by the threshold cannot run until *its* window rolls over, so an
            // earlier reset of some other window would only produce another skip.
            var anchor = blockingResetAt
                ?? (usage is not null ? UsageMetricSelector.EarliestFutureReset(usage, now) : null);

            if (anchor is { } resetAt)
            {
                var afterReset = resetAt + grace;
                if (afterReset > now && afterReset < next)
                {
                    next = afterReset;
                    reason = blockingResetAt is null
                        ? $"aligned to the next quota reset at {resetAt:u}"
                        : $"aligned to the blocking window's reset at {resetAt:u}";
                }
            }
        }

        _state.NextRunAtUtc = next;

        if (reason is null)
        {
            _logger.LogInformation("Next check at {Next:u} (interval).", next);
        }
        else
        {
            _logger.LogInformation("Next check at {Next:u} — {Reason}, ahead of the interval at {Interval:u}.",
                next,
                reason,
                byInterval);
        }

        await _stateStore.SaveAsync(_state, cancellationToken).ConfigureAwait(false);
    }

    async Task<CycleCompleted> ExecuteCycleAsync(CycleTrigger trigger, CancellationToken cancellationToken)
    {
        var settings = _settings.Current;

        if (!IsEnabled && trigger is CycleTrigger.Scheduled or CycleTrigger.Startup)
        {
            return Complete(trigger, CycleDecision.Skip(SkipReason.Disabled, "The pilot is paused."), null);
        }

        using var lease = _gate.TryEnter();
        if (lease is null)
        {
            return Complete(
                trigger,
                CycleDecision.Skip(SkipReason.AlreadyRunning, "A run is already in flight; skipping this cycle."),
                null);
        }

        // plan.md §6 step 3. Runs before usage so a misconfigured pilot fails fast and locally,
        // without spending a network call to discover it cannot launch anything anyway.
        PreflightResult? preflight = null;
        try
        {
            preflight = await _preflight.CheckAsync(settings, cancellationToken).ConfigureAwait(false);
            LastPreflight = preflight;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Preflight threw; treating the cycle as blocked.");
        }

        if (preflight is null || !preflight.CanRun)
        {
            var detail = preflight?.Summary ?? "Preflight could not be evaluated.";
            var blocked = CycleDecision.Skip(SkipReason.PreflightFailed, detail);
            _logger.LogWarning("Cycle skipped (preflight): {Detail}", detail);
            await RecordSkipAsync(blocked, cancellationToken).ConfigureAwait(false);
            return Complete(trigger, blocked, null);
        }

        CycleDecision decision;
        if (trigger == CycleTrigger.ForceRun)
        {
            decision = CycleDecision.Run(null, null, "Force run: the usage check was bypassed by the user.");
        }
        else
        {
            UsageSnapshot usage;
            try
            {
                usage = await _usage.GetUsageAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Usage lookup threw; treating usage as unavailable.");
                usage = UsageSnapshot.Unavailable(ex.Message, _timeProvider.GetUtcNow());
            }

            _lastUsage = usage;
            decision = CycleDecisionMaker.FromUsage(settings, usage);
        }

        if (!decision.ShouldRun)
        {
            _logger.LogInformation("Cycle skipped ({Reason}): {Detail}", decision.Reason, decision.Detail);
            await RecordSkipAsync(decision, cancellationToken).ConfigureAwait(false);
            return Complete(trigger, decision, null);
        }

        _logger.LogInformation("Cycle running: {Detail}", decision.Detail);

        var runner = SelectRunner(settings.LaunchMode);
        if (runner is null)
        {
            var missing = CycleDecision.Skip(
                SkipReason.PreflightFailed,
                $"No runner is registered for launch mode {settings.LaunchMode}.");
            await RecordSkipAsync(missing, cancellationToken).ConfigureAwait(false);
            return Complete(trigger, missing, null);
        }

        var progress = new Progress<RunProgress>(p => RunProgress?.Invoke(this, p));

        RunRecord record;
        try
        {
            record = await runner.RunAsync(settings, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A runner is contractually a reporter, not a thrower; a bug in one must not kill the
            // background service and with it every future cycle.
            _logger.LogError(ex, "The runner threw; recording a failed run.");
            record = (RunRecord.Start(_timeProvider.GetUtcNow()) with
            {
                EndedAt = _timeProvider.GetUtcNow(),
                Outcome = RunOutcome.Failed,
                UsageAtStart = decision.Usage,
            }).WithSummary(ex.Message);
            await _history.AppendAsync(record, cancellationToken).ConfigureAwait(false);
        }

        _state.LastRunId = record.Id;
        _state.LastSessionId = record.SessionId ?? _state.LastSessionId;
        _state.PendingResumeSessionId = record.IsResumable ? record.SessionId : null;
        _state.QuotaResumesAtUtc = record.Outcome == RunOutcome.RateLimited ? record.RateLimitResetsAt : null;
        await _stateStore.SaveAsync(_state, cancellationToken).ConfigureAwait(false);

        return Complete(trigger, decision, record);
    }

    IClaudeRunner? SelectRunner(LaunchMode mode) => _runners.FirstOrDefault(r => r.Mode == mode);

    async Task RecordSkipAsync(CycleDecision decision, CancellationToken cancellationToken)
    {
        // Skips are recorded so the history explains a quiet night, but a skip for "already running"
        // would otherwise flood the index during a long run — one line per interval is enough.
        var now = _timeProvider.GetUtcNow();
        var record = (RunRecord.Start(now) with
        {
            EndedAt = now,
            Outcome = RunOutcome.Skipped,
            SkipReason = decision.Reason,
            SkipDetail = decision.Detail,
            UsageAtStart = decision.Usage,
        }).WithSummary(decision.Detail);

        await _history.AppendAsync(record, cancellationToken).ConfigureAwait(false);
    }

    CycleCompleted Complete(CycleTrigger trigger, CycleDecision decision, RunRecord? record)
    {
        var completed = new CycleCompleted(trigger, decision, record);
        CycleCompleted?.Invoke(this, completed);
        return completed;
    }
}
