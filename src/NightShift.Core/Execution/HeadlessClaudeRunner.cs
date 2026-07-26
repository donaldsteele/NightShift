using NightShift.Core.Configuration;
using NightShift.Core.History;
using Microsoft.Extensions.Logging;

namespace NightShift.Core.Execution;

/// <summary>
/// Runs Claude Code headlessly, streams its output into the run log, and enforces the two timeouts
/// that stop an unattended run hanging forever (plan.md §5.4, §5.3.3).
/// </summary>
/// <remarks>
/// Nothing in here may throw for an expected failure. Every path — CLI missing, process died,
/// stalled, timed out — produces a <see cref="RunRecord"/>, because the caller is a background
/// scheduler with no human to show an exception to.
/// </remarks>
public sealed class HeadlessClaudeRunner : IClaudeRunner
{
    readonly IClaudeExecutableLocator _locator;
    readonly IPromptBuilder _promptBuilder;
    readonly AutoModeProbe _autoModeProbe;
    readonly WorkspaceTrustManager _trustManager;
    readonly RunHistoryStore _history;
    readonly IClaudeProcessLauncher _launcher;
    readonly ILogger<HeadlessClaudeRunner> _logger;
    readonly TimeProvider _timeProvider;

    public HeadlessClaudeRunner(
        IClaudeExecutableLocator locator,
        IPromptBuilder promptBuilder,
        AutoModeProbe autoModeProbe,
        WorkspaceTrustManager trustManager,
        RunHistoryStore history,
        IClaudeProcessLauncher launcher,
        ILogger<HeadlessClaudeRunner> logger,
        TimeProvider? timeProvider = null)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _promptBuilder = promptBuilder ?? throw new ArgumentNullException(nameof(promptBuilder));
        _autoModeProbe = autoModeProbe ?? throw new ArgumentNullException(nameof(autoModeProbe));
        _trustManager = trustManager ?? throw new ArgumentNullException(nameof(trustManager));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// How Claude Code words a rejected slash command. Matched case-insensitively on the result text.
    /// </summary>
    public const string UnknownCommandPrefix = "Unknown command:";

    public LaunchMode Mode => LaunchMode.Headless;

    /// <summary>Session id of the last run, for <see cref="ResumeStrategy.Resume"/>.</summary>
    public string? LastSessionId { get; private set; }

    public async Task<RunRecord> RunAsync(
        PilotSettings settings,
        IProgress<RunProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var record = RunRecord.Start(_timeProvider.GetUtcNow());

        var resolution = _locator.Locate(settings);
        if (!resolution.IsFound)
        {
            return await FailAsync(record, resolution.FailureReason ?? "Claude Code was not found.").ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(settings.ProjectDirectory) || !Directory.Exists(settings.ProjectDirectory))
        {
            return await FailAsync(record, $"Project directory not found: '{settings.ProjectDirectory}'.").ConfigureAwait(false);
        }

        var decision = await _autoModeProbe.DecideAsync(settings, cancellationToken).ConfigureAwait(false);
        if (decision.IsFallback)
        {
            _logger.LogWarning(
                "Permission mode {Configured} unavailable; running as {Effective}. {Explanation}",
                decision.Configured,
                decision.Effective,
                decision.Explanation);
        }

        // Advisory only: measured against Claude Code 2.1.220, a headless run in a never-trusted
        // folder proceeds anyway. A trust failure must never stop a run (plan.md §5.3.1).
        if (settings.AutoTrustProjectFolder)
        {
            var trust = await _trustManager.ApplyAsync(settings.ProjectDirectory, cancellationToken).ConfigureAwait(false);
            if (!trust.IsTrusted)
            {
                _logger.LogWarning("Could not pre-trust {Directory}: {Error}", settings.ProjectDirectory, trust.Error);
            }
        }

        var prompt = _promptBuilder.Build(settings);
        var arguments = ClaudeArgumentsBuilder.Build(
            settings,
            prompt,
            decision.Effective,
            decision.AllowedTools,
            LastSessionId);

        var start = new ClaudeProcessStart(resolution.ExecutablePath, arguments, settings.ProjectDirectory);

        if (settings.DryRun)
        {
            _logger.LogInformation("Dry run — would launch: {CommandLine}", start.ToDisplayString());
            var dryRun = record with
            {
                EndedAt = _timeProvider.GetUtcNow(),
                Outcome = RunOutcome.Skipped,
                SkipReason = SkipReason.DryRun,
                SkipDetail = start.ToDisplayString(),
                PermissionModeUsed = decision.Effective,
            };
            await _history.AppendAsync(dryRun, cancellationToken).ConfigureAwait(false);
            return dryRun;
        }

        return await ExecuteAsync(record, settings, start, decision, progress, cancellationToken).ConfigureAwait(false);
    }

    async Task<RunRecord> ExecuteAsync(
        RunRecord record,
        PilotSettings settings,
        ClaudeProcessStart start,
        PermissionModeDecision decision,
        IProgress<RunProgress>? progress,
        CancellationToken cancellationToken)
    {
        var transcriptPath = _history.TranscriptPath(record.Id);
        var maxDuration = TimeSpan.FromMinutes(settings.MaxRunDurationMinutes);
        var stallTimeout = TimeSpan.FromMinutes(settings.StallTimeoutMinutes);

        _logger.LogInformation(
            "Run {RunId} launching: {CommandLine} (mode {Mode}, max {Max}, stall {Stall})",
            record.Id,
            start.ToDisplayString(),
            decision.Effective,
            maxDuration,
            stallTimeout);

        await _history.AppendTranscriptAsync(
            record.Id,
            $"# NightShift run {record.Id}{Environment.NewLine}# {start.ToDisplayString()}{Environment.NewLine}",
            cancellationToken).ConfigureAwait(false);

        IClaudeProcess process;
        try
        {
            process = _launcher.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return await FailAsync(record, $"Could not start Claude Code: {ex.Message}").ConfigureAwait(false);
        }

        // The deadline is driven by TimeProvider rather than CancelAfter so a test can advance it
        // without waiting out a 55-minute run.
        using var deadlineCts = new CancellationTokenSource(maxDuration, _timeProvider);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineCts.Token);

        var state = new RunState(_timeProvider.GetUtcNow());
        var killReason = KillReason.None;

        using (process)
        {
            // Cancelled as soon as the run ends, so the watchdog and the stderr drain stop waiting.
            // Without this the watchdog's timer keeps the run's `finally` blocked forever whenever
            // the clock is not advancing on its own.
            using var doneCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);

            var watchdog = WatchdogAsync(process, state, stallTimeout, r => killReason = r, doneCts.Token);
            var stderr = DrainAsync(process.StandardError, state, doneCts.Token);
            var stdout = ReadStreamAsync(process, record.Id, state, progress, timeoutCts.Token);

            int? exitCode = null;
            try
            {
                await stdout.ConfigureAwait(false);
                exitCode = await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (killReason == KillReason.None)
                {
                    killReason = cancellationToken.IsCancellationRequested ? KillReason.Cancelled : KillReason.MaxDuration;
                }

                process.KillTree();
            }
            finally
            {
                state.Finished = true;
                await doneCts.CancelAsync().ConfigureAwait(false);
                await SafeAwait(watchdog).ConfigureAwait(false);
                await SafeAwait(stderr).ConfigureAwait(false);
            }

            return await FinishAsync(record, state, exitCode, killReason, decision, transcriptPath).ConfigureAwait(false);
        }
    }

    async Task ReadStreamAsync(
        IClaudeProcess process,
        string runId,
        RunState state,
        IProgress<RunProgress>? progress,
        CancellationToken cancellationToken)
    {
        var parser = new StreamJsonParser();
        var buffer = new char[8192];

        while (true)
        {
            int read;
            try
            {
                read = await process.StandardOutput.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException)
            {
                // The pipe went away with the process; treat as end of stream.
                break;
            }

            if (read == 0)
            {
                break;
            }

            state.Touch(_timeProvider.GetUtcNow());
            foreach (var streamEvent in parser.Feed(buffer.AsSpan(0, read)))
            {
                await HandleEventAsync(streamEvent, runId, state, progress, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var streamEvent in parser.Flush())
        {
            await HandleEventAsync(streamEvent, runId, state, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    async Task HandleEventAsync(
        ClaudeStreamEvent streamEvent,
        string runId,
        RunState state,
        IProgress<RunProgress>? progress,
        CancellationToken cancellationToken)
    {
        switch (streamEvent)
        {
            case ClaudeInitEvent init:
                state.SessionId = init.SessionId ?? state.SessionId;
                state.PermissionModeUsed = ParsePermissionMode(init.PermissionMode) ?? state.PermissionModeUsed;
                if (init.CavemanUnavailable)
                {
                    _logger.LogWarning(
                        "Run {RunId}: the caveman skill did not load — the prompt's /caveman line will be a no-op. {Errors}",
                        runId,
                        string.Join("; ", init.CavemanPluginErrors));
                }

                break;

            case ClaudeResultEvent result:
                state.Result = result;
                state.SessionId = result.SessionId ?? state.SessionId;
                break;

            case ClaudeMessageEvent { Role: ClaudeMessageRole.Assistant, Text.Length: > 0 } message:
                state.LastAssistantText = message.Text;
                break;

            case ClaudeRateLimitEvent rateLimit:
                _logger.LogInformation(
                    "Run {RunId}: {Window} usage now {Percent:0.##}% ({Status}).",
                    runId,
                    rateLimit.RateLimitType,
                    rateLimit.UtilizationPercent,
                    rateLimit.Status);
                break;

            case ClaudeApiRetryEvent retry:
                _logger.LogInformation("Run {RunId}: Claude is retrying (attempt {Attempt}).", runId, retry.Attempt);
                break;

            case ClaudeHookResponseEvent { Failed: true } hook:
                _logger.LogWarning("Run {RunId}: hook {Hook} failed with exit {ExitCode}.", runId, hook.HookName, hook.ExitCode);
                break;
        }

        await _history.AppendTranscriptAsync(runId, streamEvent.RawLine + Environment.NewLine, cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(new RunProgress(runId, streamEvent));
    }

    async Task WatchdogAsync(
        IClaudeProcess process,
        RunState state,
        TimeSpan stallTimeout,
        Action<KillReason> onKill,
        CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(1);
        while (!state.Finished && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (state.Finished)
            {
                return;
            }

            if (_timeProvider.GetUtcNow() - state.LastActivity >= stallTimeout)
            {
                _logger.LogError(
                    "No output for {Stall}; assuming a hidden prompt or a hung tool and killing the process tree.",
                    stallTimeout);
                onKill(KillReason.Stalled);
                process.KillTree();
                return;
            }
        }
    }

    async Task DrainAsync(TextReader reader, RunState state, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    state.StandardError.Add(line);
                    _logger.LogDebug("claude stderr: {Line}", line);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The process is gone; stderr content is diagnostic only.
        }
    }

    async Task<RunRecord> FinishAsync(
        RunRecord record,
        RunState state,
        int? exitCode,
        KillReason killReason,
        PermissionModeDecision decision,
        string transcriptPath)
    {
        // Claude Code answers an unrecognised slash command with is_error=false and exit code 0, so
        // the prompt is silently swallowed and nothing runs. Measured 2026-07-26: `/caveman full`
        // yields result "Unknown command: /caveman" on a "successful" run. Left undetected, an
        // unattended pilot burns every slot achieving nothing while the history says Success.
        var unknownCommand = state.Result?.ResultText is { } text
            && text.StartsWith(UnknownCommandPrefix, StringComparison.OrdinalIgnoreCase);

        var outcome = killReason switch
        {
            KillReason.Stalled or KillReason.MaxDuration => RunOutcome.TimedOut,
            KillReason.Cancelled => RunOutcome.Failed,
            _ when state.Result is { IsError: true } => RunOutcome.Failed,
            _ when unknownCommand => RunOutcome.Failed,
            _ when exitCode == 0 => RunOutcome.Success,
            _ => RunOutcome.Failed,
        };

        var detail = killReason switch
        {
            KillReason.Stalled => "Stalled: no stream events within the stall timeout.",
            KillReason.MaxDuration => "Exceeded the maximum run duration.",
            KillReason.Cancelled => "Cancelled.",
            _ when unknownCommand =>
                $"Claude Code did not recognise a slash command in the prompt ({state.Result?.ResultText}). " +
                "The prompt was swallowed and nothing ran. Check the caveman plugin is installed and " +
                "that the command is namespaced, e.g. /caveman:caveman.",
            _ => null,
        };

        if (unknownCommand)
        {
            _logger.LogError("Run {RunId}: {Detail}", record.Id, detail);
        }

        var summary = state.Result?.ResultText ?? state.LastAssistantText ?? detail;
        if (summary is null && state.StandardError.Count > 0)
        {
            summary = state.StandardError[^1];
        }

        var finished = (record with
        {
            EndedAt = _timeProvider.GetUtcNow(),
            Outcome = outcome,
            SkipDetail = detail,
            SessionId = state.SessionId,
            CostUsd = state.Result?.TotalCostUsd,
            ExitCode = exitCode,
            PermissionModeUsed = state.PermissionModeUsed ?? decision.Effective,
            TranscriptPath = transcriptPath,
        }).WithSummary(summary);

        LastSessionId = state.SessionId ?? LastSessionId;

        _logger.LogInformation(
            "Run {RunId} finished: {Outcome} (exit {ExitCode}, {Cost:0.####} USD, mode {Mode}).",
            finished.Id,
            finished.Outcome,
            finished.ExitCode,
            finished.CostUsd ?? 0,
            finished.PermissionModeUsed);

        await _history.AppendAsync(finished).ConfigureAwait(false);
        return finished;
    }

    async Task<RunRecord> FailAsync(RunRecord record, string reason)
    {
        _logger.LogError("Run {RunId} could not start: {Reason}", record.Id, reason);

        var failed = (record with
        {
            EndedAt = _timeProvider.GetUtcNow(),
            Outcome = RunOutcome.Failed,
            SkipDetail = reason,
        }).WithSummary(reason);

        await _history.AppendAsync(failed).ConfigureAwait(false);
        return failed;
    }

    static async Task SafeAwait(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    static PermissionMode? ParsePermissionMode(string? value) => value switch
    {
        "auto" => PermissionMode.Auto,
        "acceptEdits" => PermissionMode.AcceptEdits,
        "bypassPermissions" => PermissionMode.BypassPermissions,
        _ => null,
    };

    enum KillReason
    {
        None,
        Stalled,
        MaxDuration,
        Cancelled,
    }

    sealed class RunState(DateTimeOffset startedAt)
    {
        long _lastActivityTicks = startedAt.UtcTicks;

        public DateTimeOffset LastActivity => new(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);

        public volatile bool Finished;

        public string? SessionId { get; set; }

        public PermissionMode? PermissionModeUsed { get; set; }

        public string? LastAssistantText { get; set; }

        public ClaudeResultEvent? Result { get; set; }

        public List<string> StandardError { get; } = [];

        public void Touch(DateTimeOffset at) => Interlocked.Exchange(ref _lastActivityTicks, at.UtcTicks);
    }
}
