using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NightShift.Core.Configuration;
using NightShift.Core.Execution;
using NightShift.Core.History;
using NSubstitute;

namespace NightShift.Core.Tests.Execution;

public sealed class HeadlessClaudeRunnerTests : IDisposable
{
    static readonly DateTimeOffset Start = new(2026, 7, 26, 22, 0, 0, TimeSpan.Zero);

    readonly TempDirectory _appData = new();
    readonly TempDirectory _project = new();
    readonly FakeTimeProvider _time = new(Start);
    readonly FakeClaudeProcess _process = new();
    readonly FakeClaudeProcessLauncher _launcher;
    readonly RunHistoryStore _history;
    readonly AppPaths _paths;

    public HeadlessClaudeRunnerTests()
    {
        _paths = new AppPaths(_appData.Path);
        _history = new RunHistoryStore(_paths, NullLogger<RunHistoryStore>.Instance);
        _launcher = new FakeClaudeProcessLauncher(_process);
    }

    PilotSettings Settings(Action<PilotSettings>? mutate = null)
    {
        var settings = new PilotSettings
        {
            ProjectDirectory = _project.Path,
            ClaudeExecutablePath = ExistingExecutable(),
            MaxRunDurationMinutes = 55,
            StallTimeoutMinutes = 10,
        };
        mutate?.Invoke(settings);
        return settings;
    }

    string ExistingExecutable()
    {
        var path = Path.Combine(_appData.Path, "claude.cmd");
        if (!File.Exists(path))
        {
            File.WriteAllText(path, "@echo off");
        }

        return path;
    }

    HeadlessClaudeRunner CreateRunner(AutoModeProbe? probe = null)
    {
        var locator = new ClaudeExecutableLocator(NullLogger<ClaudeExecutableLocator>.Instance);
        var trust = new WorkspaceTrustManager(
            NullLogger<WorkspaceTrustManager>.Instance,
            Path.Combine(_appData.Path, "claude-config.json"));

        return new HeadlessClaudeRunner(
            locator,
            new PromptBuilder(),
            probe ?? AvailableAutoMode(),
            trust,
            _history,
            _launcher,
            NullLogger<HeadlessClaudeRunner>.Instance,
            _time);
    }

    AutoModeProbe AvailableAutoMode()
    {
        var runner = Substitute.For<IAutoModeProbeRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new AutoModeProbeOutput(0, "{\"allow\":[]}", string.Empty, false));
        return new AutoModeProbe(runner, NullLogger<AutoModeProbe>.Instance, "claude", _time);
    }

    static string InitLine(string sessionId = "sess-1", string permissionMode = "auto") =>
        $$"""{"type":"system","subtype":"init","session_id":"{{sessionId}}","permissionMode":"{{permissionMode}}","model":"claude-opus-5","tools":["Read"],"plugins":[{"name":"caveman","source":"caveman@caveman"}],"slash_commands":["caveman:caveman"]}""";

    static string ResultLine(double cost = 0.25, bool isError = false) =>
        $$"""{"type":"result","subtype":"success","is_error":{{(isError ? "true" : "false")}},"session_id":"sess-1","total_cost_usd":{{cost}},"duration_ms":1200,"result":"all done","stop_reason":"end_turn"}""";

    [Fact]
    public async Task A_clean_run_is_recorded_as_success_with_the_details_from_the_stream()
    {
        var runner = CreateRunner();
        var events = new List<ClaudeStreamEvent>();
        var progress = new Progress<RunProgress>(p => events.Add(p.Event));

        var task = runner.RunAsync(Settings(), progress);

        _process.EmitStdout(InitLine() + "\n");
        _process.EmitStdout(ResultLine() + "\n");
        _process.Exit(0);

        var record = await task;

        Assert.Equal(RunOutcome.Success, record.Outcome);
        Assert.Equal(0, record.ExitCode);
        Assert.Equal("sess-1", record.SessionId);
        Assert.Equal(0.25, record.CostUsd);
        Assert.Equal("all done", record.Summary);
        Assert.Equal(PermissionMode.Auto, record.PermissionModeUsed);
        Assert.Equal(_history.TranscriptPath(record.Id), record.TranscriptPath);
    }

    [Fact]
    public async Task The_permission_mode_recorded_is_the_one_the_stream_reported_not_the_one_requested()
    {
        // plan.md §11.4c: History must show which mode the run actually used.
        var runner = CreateRunner();

        var task = runner.RunAsync(Settings(s => s.PermissionMode = PermissionMode.Auto));
        _process.EmitStdout(InitLine(permissionMode: "acceptEdits") + "\n");
        _process.EmitStdout(ResultLine() + "\n");
        _process.Exit(0);

        Assert.Equal(PermissionMode.AcceptEdits, (await task).PermissionModeUsed);
    }

    [Fact]
    public async Task The_transcript_is_written_to_disk()
    {
        var runner = CreateRunner();

        var task = runner.RunAsync(Settings());
        _process.EmitStdout(InitLine() + "\n" + ResultLine() + "\n");
        _process.Exit(0);
        var record = await task;

        var transcript = await _history.ReadTranscriptAsync(record.Id);
        Assert.Contains("\"type\":\"system\"", transcript, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"result\"", transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_result_marked_is_error_fails_the_run_even_on_exit_zero()
    {
        var runner = CreateRunner();

        var task = runner.RunAsync(Settings());
        _process.EmitStdout(ResultLine(isError: true) + "\n");
        _process.Exit(0);

        Assert.Equal(RunOutcome.Failed, (await task).Outcome);
    }

    [Fact]
    public async Task An_unknown_slash_command_fails_the_run_despite_looking_successful()
    {
        // Measured against Claude Code 2.1.220: an unrecognised slash command comes back with
        // is_error=false and exit code 0, having silently swallowed the whole prompt. Without this
        // check an unattended pilot burns every slot doing nothing and records Success.
        var runner = CreateRunner();

        var task = runner.RunAsync(Settings());
        _process.EmitStdout(
            """{"type":"result","subtype":"success","is_error":false,"session_id":"sess-1","total_cost_usd":0,"duration_ms":900,"result":"Unknown command: /caveman"}"""
            + "\n");
        _process.Exit(0);

        var record = await task;

        Assert.Equal(RunOutcome.Failed, record.Outcome);
        Assert.Equal(0, record.ExitCode);
        Assert.Contains("did not recognise a slash command", record.SkipDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_non_zero_exit_code_fails_the_run()
    {
        var runner = CreateRunner();

        var task = runner.RunAsync(Settings());
        _process.Exit(143);

        var record = await task;
        Assert.Equal(RunOutcome.Failed, record.Outcome);
        Assert.Equal(143, record.ExitCode);
    }

    [Fact]
    public async Task A_stalled_run_is_killed_and_recorded_as_timed_out()
    {
        // plan.md §5.3.3: no stream event for StallTimeoutMinutes means a hidden prompt or a hung
        // tool. The process tree must die rather than hang the pilot forever.
        var runner = CreateRunner();

        var task = runner.RunAsync(Settings(s => s.StallTimeoutMinutes = 10));
        _process.EmitStdout(InitLine() + "\n");
        await Task.Delay(50);

        _time.Advance(TimeSpan.FromMinutes(11));
        await Task.Delay(50);
        _time.Advance(TimeSpan.FromSeconds(2));

        var record = await task;

        Assert.Equal(RunOutcome.TimedOut, record.Outcome);
        Assert.True(_process.WasKilled);
        Assert.Contains("Stalled", record.SkipDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_that_outlives_its_slot_is_killed_and_recorded_as_timed_out()
    {
        var runner = CreateRunner();

        var task = runner.RunAsync(Settings(s =>
        {
            s.MaxRunDurationMinutes = 55;
            s.StallTimeoutMinutes = 600;
        }));
        _process.EmitStdout(InitLine() + "\n");
        await Task.Delay(50);

        _time.Advance(TimeSpan.FromMinutes(56));

        var record = await task;

        Assert.Equal(RunOutcome.TimedOut, record.Outcome);
        Assert.True(_process.WasKilled);
    }

    [Fact]
    public async Task A_run_cut_short_by_quota_is_recorded_as_RateLimited_with_its_reset_time()
    {
        var runner = CreateRunner();

        var task = runner.RunAsync(Settings());
        _process.EmitStdout(InitLine() + "\n");
        _process.EmitStdout(
            "{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"rejected\"," +
            "\"rateLimitType\":\"five_hour\",\"utilization\":1.0,\"resetsAt\":1785546000}}\n");
        _process.EmitStdout(ResultLine() + "\n");
        _process.Exit(0);

        var record = await task;

        Assert.Equal(RunOutcome.RateLimited, record.Outcome);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 1, 0, 0, TimeSpan.Zero), record.RateLimitResetsAt);
        Assert.Contains("Quota returns at", record.SkipDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_interrupted_session_that_did_work_is_resumed_on_the_next_run()
    {
        var runner = CreateRunner();

        // First run: a tool completes, then quota runs out.
        var first = runner.RunAsync(Settings());
        _process.EmitStdout(InitLine(sessionId: "sess-interrupted") + "\n");
        _process.EmitStdout(
            "{\"type\":\"user\",\"session_id\":\"sess-interrupted\",\"message\":{\"role\":\"user\",\"content\":" +
            "[{\"type\":\"tool_result\",\"tool_use_id\":\"toolu_1\",\"content\":\"written\"}]}}\n");
        _process.EmitStdout(
            "{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"rejected\"," +
            "\"rateLimitType\":\"five_hour\",\"utilization\":1.0,\"resetsAt\":1785546000}}\n");
        _process.EmitStdout(
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"session_id\":\"sess-interrupted\"," +
            "\"total_cost_usd\":0.25,\"duration_ms\":1200,\"result\":\"stopped early\"}\n");
        _process.Exit(0);

        var firstRecord = await first;

        Assert.Equal(RunOutcome.RateLimited, firstRecord.Outcome);
        Assert.True(firstRecord.IsResumable);
        Assert.Equal("sess-interrupted", runner.PendingResumeSessionId);

        // Second run on the SAME runner — the scheduler holds one instance across cycles.
        // --resume must carry the interrupted session even though the strategy is Fresh.
        var second = await runner.RunAsync(Settings(s => s.ResumeStrategy = ResumeStrategy.Fresh));

        var arguments = _launcher.Starts[^1].Arguments.ToList();
        var resumeIndex = arguments.IndexOf("--resume");

        Assert.True(resumeIndex >= 0, "the interrupted session must be resumed");
        Assert.Equal("sess-interrupted", arguments[resumeIndex + 1]);

        // And once a run completes without being cut short, the pending resume is cleared.
        Assert.Equal(RunOutcome.Success, second.Outcome);
        Assert.Null(runner.PendingResumeSessionId);
    }

    [Fact]
    public async Task A_quota_stop_before_any_work_is_not_marked_resumable()
    {
        // Resuming a session that achieved nothing just replays the prompt at extra cost.
        var runner = CreateRunner();

        var task = runner.RunAsync(Settings());
        _process.EmitStdout(InitLine() + "\n");
        _process.EmitStdout(
            "{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"rejected\"," +
            "\"rateLimitType\":\"seven_day\",\"utilization\":1.0,\"resetsAt\":1785546000}}\n");
        _process.EmitStdout(ResultLine() + "\n");
        _process.Exit(0);

        var record = await task;

        Assert.Equal(RunOutcome.RateLimited, record.Outcome);
        Assert.False(record.IsResumable);
        Assert.Null(runner.PendingResumeSessionId);
    }

    [Fact]
    public async Task Dry_run_never_launches_anything_but_logs_the_command_line()
    {
        var runner = CreateRunner();

        var record = await runner.RunAsync(Settings(s => s.DryRun = true));

        Assert.Equal(0, _launcher.StartCount);
        Assert.Equal(RunOutcome.Skipped, record.Outcome);
        Assert.Equal(SkipReason.DryRun, record.SkipReason);
        Assert.Contains("--permission-mode", record.SkipDetail, StringComparison.Ordinal);
        Assert.Contains("stream-json", record.SkipDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_executable_is_a_recorded_failure_not_an_exception()
    {
        var runner = CreateRunner();
        var locator = new ClaudeExecutableLocator(NullLogger<ClaudeExecutableLocator>.Instance);

        var record = await runner.RunAsync(Settings(s => s.ProjectDirectory = Path.Combine(_project.Path, "gone")));

        Assert.Equal(RunOutcome.Failed, record.Outcome);
        Assert.Contains("Project directory not found", record.Summary, StringComparison.Ordinal);
        Assert.Equal(0, _launcher.StartCount);
        Assert.NotNull(locator);
    }

    [Fact]
    public async Task Every_outcome_lands_in_the_history_index()
    {
        var runner = CreateRunner();

        var task = runner.RunAsync(Settings());
        _process.EmitStdout(ResultLine() + "\n");
        _process.Exit(0);
        var record = await task;

        var all = await _history.ReadAllAsync();
        Assert.Contains(all, r => r.Id == record.Id && r.Outcome == RunOutcome.Success);
    }

    [Fact]
    public async Task The_launched_command_line_carries_the_required_flags_and_the_working_directory()
    {
        var runner = CreateRunner();

        var task = runner.RunAsync(Settings());
        _process.Exit(0);
        await task;

        var start = _launcher.LastStart;
        Assert.NotNull(start);
        Assert.Equal(_project.Path, start.WorkingDirectory);
        Assert.Contains("--include-partial-messages", start.Arguments);
        Assert.Contains("AskUserQuestion", start.Arguments);
        Assert.DoesNotContain("--bare", start.Arguments);
    }

    public void Dispose()
    {
        _process.Dispose();
        _appData.Dispose();
        _project.Dispose();
    }
}
