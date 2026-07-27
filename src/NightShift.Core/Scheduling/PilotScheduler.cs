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
/// <param name="UsageAfterRun">
/// Usage re-read once the run finished, or null when the cycle never launched anything (or the
/// re-read failed). <see cref="CycleDecision"/>'s snapshot is the figure the gate <em>decided</em>
/// on, taken before the run; this is what the quota looks like now that the run has spent some of
/// it. The dashboard prefers this one, so gauges stop showing an hour-old reading.
/// </param>
public sealed record CycleCompleted(
    CycleTrigger Trigger,
    CycleDecision Decision,
    RunRecord? Record,
    UsageSnapshot? UsageAfterRun = null);

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

    /// <summary>
    /// The reason of the last skip written to history, so a run of identical skips collapses to one
    /// row. Cleared whenever a real run is recorded. See <c>RecordSkipAsync</c>.
    /// </summary>
    SkipReason? _lastRecordedSkip;

    /// <summary>Cancellation for the run in flight, so <see cref="StopCurrentRun"/> can reach it.</summary>
    CancellationTokenSource? _currentRunCts;

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

    /// <summary>Id of the run currently in flight, or null. Lets the UI label its Stop button.</summary>
    public string? CurrentRunId { get; private set; }

    /// <summary>
    /// Cancels the run in flight, whoever started it (plan.md §9.1's "Stop run" button).
    /// </summary>
    /// <remarks>
    /// Without this, a scheduled run could only be stopped by killing the app: cancellation
    /// otherwise travels solely down the token passed to <see cref="RunNowAsync"/>, which a
    /// background cycle does not have. The runner treats the cancellation as a kill-the-process-tree
    /// signal and still records a <see cref="RunRecord"/>, so a stopped run is history, not a gap.
    /// </remarks>
    /// <returns>True if a run was in flight and has been asked to stop.</returns>
    public bool StopCurrentRun()
    {
        var cts = Volatile.Read(ref _currentRunCts);
        if (cts is null)
        {
            return false;
        }

        _logger.LogInformation("Stopping run {RunId} at the user's request.", CurrentRunId);
        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // The run finished between the read and the cancel.
            return false;
        }
    }

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
            // Same cap as the in-session path: a persisted reset time is no more trustworthy than
            // the one that produced it, and a restart is a natural moment to re-check.
            var reported = quotaReturnsAt + TimeSpan.FromMinutes(settings.QuotaResetGraceMinutes);
            var cap = now + TimeSpan.FromHours(settings.MaxQuotaWaitHours);
            _state.NextRunAtUtc = reported > cap ? cap : reported;
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
                _lastUsage = await _usage.GetUsageAsync(cancellationToken: stoppingToken).ConfigureAwait(false);
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
                // Post-run first: its reset times are the newest we have, and a window that rolled
                // over during the run is exactly what the next check wants to be anchored to.
                cycle.UsageAfterRun ?? cycle.Decision.Usage ?? _lastUsage,
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
            // Capped, because `resets_at` cannot be trusted as "when quota returns" — see
            // MaxQuotaWaitHours. Waking early costs one cached usage lookup; waking a week late
            // costs every night in between.
            var reportedWait = quotaReturnsAt + grace;
            var cap = now + TimeSpan.FromHours(settings.MaxQuotaWaitHours);
            var capped = reportedWait > cap;

            _state.NextRunAtUtc = capped ? cap : reportedWait;

            if (capped)
            {
                _logger.LogWarning(
                    "Run {RunId} was cut short by quota, which claims to return at {Reported:u}. " +
                    "That is further out than the {Cap}h cap, and reset times are known to be unreliable, " +
                    "so the next check is at {Next:u} instead.",
                    record.Id,
                    reportedWait,
                    settings.MaxQuotaWaitHours,
                    _state.NextRunAtUtc);
            }
            else
            {
                _logger.LogWarning(
                    "Run {RunId} was cut short by quota. Waiting until {Next:u}, when the window rolls over.",
                    record.Id,
                    _state.NextRunAtUtc);
            }

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

        // Preflight has just read the plan file, so it is the only thing that knows what `Auto`
        // resolved to. Pinning it onto the settings the runner receives is what lets the prompt state
        // checkbox or milestone conventions without every runner learning to detect it itself.
        settings = settings with { PlanFormat = preflight.PlanFormat };

        // Always read usage, even for a force run. Force bypasses the *gate*, not the reading: a
        // forced run that left the gauges untouched was the other half of "the number never
        // refreshes", and the reset times feed the next schedule either way.
        var usage = await ReadUsageAsync("before the run", cancellationToken).ConfigureAwait(false);
        _lastUsage = usage;

        // A finished plan is not a preflight failure, so it is an amber row rather than a red one
        // — and amber rows never stop a run. The result was that a plan with everything ticked
        // still launched Claude on every interval, spending a slot and a chunk of the weekly quota
        // to be told there was nothing to do. `PreflightChecker.PlanItemsCheck`'s own remarks name
        // this exact cost; the check just had no way to act on it.
        //
        // After the usage read rather than before it, so the gauges and reset times still refresh
        // on a cycle that does not run. Force run goes through, as it does at every other gate:
        // the user asking for it explicitly has already been told there is nothing to do.
        if (trigger != CycleTrigger.ForceRun
            && preflight.PlanItems is { Total: > 0, HasRemainingWork: false } counts)
        {
            var noun = counts.Noun;
            var detail = counts.Blocked > 0
                ? $"Nothing to do: {counts.Summary}, and the remaining {counts.Blocked} " +
                  $"{noun}{(counts.Blocked == 1 ? " is" : "s are")} marked blocked."
                : $"Nothing to do: {counts.Summary}.";

            var nothing = CycleDecision.Skip(SkipReason.NoWorkLeft, detail);
            _logger.LogInformation("Cycle skipped (no work left): {Detail}", detail);
            await RecordSkipAsync(nothing, cancellationToken).ConfigureAwait(false);
            return Complete(trigger, nothing, null);
        }

        var decision = trigger == CycleTrigger.ForceRun
            ? CycleDecision.Run(usage, usage.Select(settings.UsageMetric), "Force run: the usage check was bypassed by the user.")
            : CycleDecisionMaker.FromUsage(settings, usage);

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

        var progress = new Progress<RunProgress>(p =>
        {
            CurrentRunId ??= p.RunId;
            RunProgress?.Invoke(this, p);
        });

        // Linked so StopCurrentRun() can reach a run the background loop owns; a scheduled cycle has
        // no caller-supplied token, so without this the only way to stop one is to kill the app.
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Volatile.Write(ref _currentRunCts, runCts);

        RunRecord record;
        try
        {
            record = await runner.RunAsync(settings, progress, runCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Stopped by the user rather than by shutdown: record it so the history explains the gap.
            _logger.LogInformation("Run stopped by the user.");
            record = (RunRecord.Start(_timeProvider.GetUtcNow()) with
            {
                EndedAt = _timeProvider.GetUtcNow(),
                Outcome = RunOutcome.Failed,
                SkipDetail = "Stopped by the user.",
                UsageAtStart = decision.Usage,
            }).WithSummary("Stopped by the user.");
            await _history.AppendAsync(record, CancellationToken.None).ConfigureAwait(false);
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
        finally
        {
            Volatile.Write(ref _currentRunCts, null);
            CurrentRunId = null;
        }

        _state.LastRunId = record.Id;
        _state.LastSessionId = record.SessionId ?? _state.LastSessionId;
        _state.PendingResumeSessionId = record.IsResumable ? record.SessionId : null;
        _state.QuotaResumesAtUtc = record.Outcome == RunOutcome.RateLimited ? record.RateLimitResetsAt : null;
        await _stateStore.SaveAsync(_state, cancellationToken).ConfigureAwait(false);

        // The run just spent quota, so everything read before it is now wrong. Re-read before this
        // cycle is declared over: the dashboard shows the result, and `ScheduleNextAsync` anchors the
        // next check against the reset times we have just confirmed rather than hour-old ones.
        // CancellationToken.None because a shutdown must not leave the figures wrong — the call is
        // bounded by the HTTP client's own timeout.
        var usageAfterRun = await ReadUsageAsync("after the run", CancellationToken.None).ConfigureAwait(false);
        _lastUsage = usageAfterRun;

        // A run recorded is the end of a quiet stretch; the next skip deserves its own history line.
        _lastRecordedSkip = null;

        return Complete(trigger, decision, record, usageAfterRun);
    }

    IClaudeRunner? SelectRunner(LaunchMode mode) => _runners.FirstOrDefault(r => r.Mode == mode);

    /// <summary>
    /// One usage lookup that reports rather than throws, forcing past the provider cache.
    /// </summary>
    /// <remarks>
    /// Forced because both call sites exist to answer "what is the quota <em>now</em>": serving a
    /// cached figure to the check that decides whether to burn an hour of quota, or to the refresh
    /// that exists because the last figure looked stale, defeats the point. The cache still protects
    /// against bursts from the UI. Rate-limit backoff is unaffected — see
    /// <see cref="IUsageProvider.GetUsageAsync"/>.
    /// </remarks>
    async Task<UsageSnapshot> ReadUsageAsync(string when, CancellationToken cancellationToken)
    {
        try
        {
            return await _usage.GetUsageAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Usage lookup {When} threw; treating usage as unavailable.", when);
            return UsageSnapshot.Unavailable(ex.Message, _timeProvider.GetUtcNow());
        }
    }

    async Task RecordSkipAsync(CycleDecision decision, CancellationToken cancellationToken)
    {
        // Skips are recorded so the history explains a quiet night, but only the *first* of a run of
        // identical ones: at the default five-minute interval a blocked pilot would otherwise write
        // 288 rows a day into a 200-row index and prune every real run out of it. The state is
        // in-memory, so a restart costs one extra row — cheaper than reading the index every tick.
        if (_lastRecordedSkip == decision.Reason)
        {
            _logger.LogDebug("Skip ({Reason}) repeated; not recording another history row.", decision.Reason);
            return;
        }

        _lastRecordedSkip = decision.Reason;

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

    CycleCompleted Complete(
        CycleTrigger trigger,
        CycleDecision decision,
        RunRecord? record,
        UsageSnapshot? usageAfterRun = null)
    {
        var completed = new CycleCompleted(trigger, decision, record, usageAfterRun);
        CycleCompleted?.Invoke(this, completed);
        return completed;
    }
}
