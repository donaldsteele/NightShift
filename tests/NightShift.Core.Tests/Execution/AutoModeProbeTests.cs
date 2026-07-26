using NightShift.Core.Configuration;
using NightShift.Core.Execution;
using NightShift.Core.History;
using Microsoft.Extensions.Logging.Abstractions;

namespace NightShift.Core.Tests.Execution;

/// <summary>
/// The availability ladder from plan.md §5.3.2. Every case drives the probe through a fake process
/// runner: <b>nothing here ever spawns the real <c>claude</c></b>.
/// </summary>
public sealed class AutoModeProbeTests
{
    static readonly DateTimeOffset Now = new(2026, 7, 26, 15, 0, 0, TimeSpan.Zero);

    const string ConfigJson = """{ "environment": { "allow": ["$defaults"] }, "enabled": true }""";

    static AutoModeProbe Probe(FakeRunner runner, string executable = "claude") =>
        new(runner, NullLogger<AutoModeProbe>.Instance, executable, new FixedTimeProvider(Now));

    static PilotSettings Settings(
        PermissionMode mode = PermissionMode.Auto,
        string allowedTools = PilotSettings.DefaultAllowedTools,
        bool skipPermissions = false) =>
        new()
        {
            PermissionMode = mode,
            AllowedTools = allowedTools,
            SkipPermissionsFallback = skipPermissions,
        };

    // ── Probing ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Nothing_is_probed_until_it_is_asked_for()
    {
        var runner = FakeRunner.Succeeding(ConfigJson);

        Assert.Equal(AutoModeAvailability.Unknown, Probe(runner).Status.Availability);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task Exit_code_zero_with_parseable_json_means_available()
    {
        var runner = FakeRunner.Succeeding("Reading config...\n" + ConfigJson);

        var status = await Probe(runner).GetStatusAsync();

        Assert.True(status.IsAvailable);
        Assert.Equal(Now, status.ProbedAt);
        Assert.Null(status.Detail);
    }

    [Fact]
    public async Task The_probe_runs_claude_auto_mode_config()
    {
        var runner = FakeRunner.Succeeding(ConfigJson);

        await Probe(runner, executable: @"C:\tools\claude.cmd").GetStatusAsync();

        Assert.Equal(@"C:\tools\claude.cmd", runner.LastExecutable);
        Assert.Equal(["auto-mode", "config"], runner.LastArguments);
        Assert.Equal(AutoModeProbe.ProbeTimeout, runner.LastTimeout);
    }

    [Fact]
    public async Task Exit_code_zero_without_json_means_unavailable()
    {
        // An older CLI prints usage text for an unknown subcommand and still exits 0.
        var runner = FakeRunner.Succeeding("Usage: claude [options] [command]\n\nUnknown command.");

        var status = await Probe(runner).GetStatusAsync();

        Assert.Equal(AutoModeAvailability.Unavailable, status.Availability);
        Assert.Contains("did not print JSON", status.Detail);
    }

    [Fact]
    public async Task A_non_zero_exit_code_means_unavailable_and_reports_the_reason()
    {
        var runner = FakeRunner.Failing(1, "auto mode is not enabled for this organization\nstack...");

        var status = await Probe(runner).GetStatusAsync();

        Assert.Equal(AutoModeAvailability.Unavailable, status.Availability);
        Assert.Contains("not enabled for this organization", status.Detail);
        Assert.DoesNotContain("stack...", status.Detail);
    }

    [Fact]
    public async Task A_timeout_means_unavailable()
    {
        var runner = new FakeRunner(_ => new AutoModeProbeOutput(-1, "", "", TimedOut: true));

        var status = await Probe(runner).GetStatusAsync();

        Assert.Equal(AutoModeAvailability.Unavailable, status.Availability);
        Assert.Contains("did not finish", status.Detail);
    }

    [Fact]
    public async Task A_cli_that_will_not_start_means_unavailable_not_an_exception()
    {
        var runner = new FakeRunner(_ => new AutoModeProbeOutput(-1, "", "", Started: false));

        var status = await Probe(runner).GetStatusAsync();

        Assert.Equal(AutoModeAvailability.Unavailable, status.Availability);
        Assert.Contains("could not be started", status.Detail);
    }

    [Fact]
    public async Task A_runner_that_throws_is_swallowed_into_unavailable()
    {
        var runner = FakeRunner.Throwing(new InvalidOperationException("no such file"));

        var status = await Probe(runner).GetStatusAsync();

        Assert.Equal(AutoModeAvailability.Unavailable, status.Availability);
        Assert.Contains("no such file", status.Detail);
    }

    [Fact]
    public async Task The_result_is_cached()
    {
        var runner = FakeRunner.Succeeding(ConfigJson);
        var probe = Probe(runner);

        await probe.GetStatusAsync();
        await probe.GetStatusAsync();
        await probe.DecideAsync(Settings());

        Assert.Equal(1, runner.Calls);
        Assert.True(probe.Status.IsAvailable);
    }

    [Fact]
    public async Task Re_probing_clears_the_cache_and_can_flip_the_answer()
    {
        var available = false;
        var runner = new FakeRunner(_ => available
            ? new AutoModeProbeOutput(0, ConfigJson, "")
            : new AutoModeProbeOutput(1, "", "auto mode unavailable"));
        var probe = Probe(runner);

        Assert.False((await probe.GetStatusAsync()).IsAvailable);

        available = true;

        Assert.True((await probe.ReprobeAsync()).IsAvailable);
        Assert.Equal(2, runner.Calls);
        Assert.True(probe.Status.IsAvailable);
    }

    // ── The ladder ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rung_one_auto_is_used_when_it_is_available()
    {
        var decision = await Probe(FakeRunner.Succeeding(ConfigJson)).DecideAsync(Settings());

        Assert.Equal(PermissionMode.Auto, decision.Effective);
        Assert.False(decision.IsFallback);
        Assert.Null(decision.Explanation);
        Assert.Equal(PilotSettings.DefaultAllowedTools, decision.AllowedTools);
    }

    [Fact]
    public async Task Rung_two_is_accept_edits_with_a_broad_tool_list_when_auto_is_unavailable()
    {
        var runner = FakeRunner.Failing(1, "auto mode requires a supported model");

        var decision = await Probe(runner).DecideAsync(Settings());

        Assert.Equal(PermissionMode.Auto, decision.Configured);
        Assert.Equal(PermissionMode.AcceptEdits, decision.Effective);
        Assert.True(decision.IsFallback);
        Assert.Contains("acceptEdits", decision.Explanation);
        Assert.Contains("supported model", decision.Explanation);

        var tools = decision.AllowedTools.Split(',');
        Assert.Contains("Bash", tools);
        Assert.Contains("WebFetch", tools);
        Assert.Contains("Task", tools);
        Assert.True(tools.Length > PilotSettings.DefaultAllowedTools.Split(',').Length);
    }

    [Fact]
    public async Task Rung_three_bypass_is_never_reached_by_falling_back()
    {
        var runner = FakeRunner.Failing(1, "nope");

        var decision = await Probe(runner).DecideAsync(Settings());

        Assert.NotEqual(PermissionMode.BypassPermissions, decision.Effective);
    }

    [Fact]
    public async Task Rung_three_bypass_is_used_when_it_is_configured_outright()
    {
        var runner = FakeRunner.Succeeding(ConfigJson);

        var decision = await Probe(runner).DecideAsync(Settings(PermissionMode.BypassPermissions));

        Assert.Equal(PermissionMode.BypassPermissions, decision.Effective);
        Assert.False(decision.IsFallback);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task Skip_permissions_forces_bypass_because_the_flag_implies_it()
    {
        var runner = FakeRunner.Succeeding(ConfigJson);

        var decision = await Probe(runner).DecideAsync(Settings(skipPermissions: true));

        Assert.Equal(PermissionMode.Auto, decision.Configured);
        Assert.Equal(PermissionMode.BypassPermissions, decision.Effective);
        Assert.True(decision.IsFallback);
        Assert.Contains("bypassPermissions", decision.Explanation);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task A_deliberately_configured_accept_edits_still_gets_the_broad_list_and_no_probe()
    {
        var runner = FakeRunner.Succeeding(ConfigJson);

        var decision = await Probe(runner).DecideAsync(Settings(PermissionMode.AcceptEdits));

        Assert.Equal(PermissionMode.AcceptEdits, decision.Effective);
        Assert.False(decision.IsFallback);
        Assert.Contains("WebFetch", decision.AllowedTools);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task An_empty_allowed_tools_setting_falls_back_to_the_default_list()
    {
        var decision = await Probe(FakeRunner.Succeeding(ConfigJson))
            .DecideAsync(Settings(allowedTools: "   "));

        Assert.Equal(PilotSettings.DefaultAllowedTools, decision.AllowedTools);
    }

    [Fact]
    public void Broadening_keeps_the_configured_tools_first_and_deduplicates_case_insensitively()
    {
        var broadened = AutoModeProbe.Broaden("Zed, bash ,Read");
        var tools = broadened.Split(',');

        Assert.Equal("Zed", tools[0]);
        Assert.Equal("bash", tools[1]);
        Assert.Equal("Read", tools[2]);
        Assert.Single(tools, tool => string.Equals(tool, "Bash", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("", tools);
    }

    [Fact]
    public async Task The_chosen_mode_is_reportable_on_a_run_record()
    {
        var decision = await Probe(FakeRunner.Failing(1, "unavailable")).DecideAsync(Settings());

        var record = new RunRecord { PermissionModeUsed = decision.Effective };

        Assert.Equal(PermissionMode.AcceptEdits, record.PermissionModeUsed);
    }

    sealed class FakeRunner(Func<IReadOnlyList<string>, AutoModeProbeOutput> respond) : IAutoModeProbeRunner
    {
        public int Calls { get; private set; }

        public string? LastExecutable { get; private set; }

        public IReadOnlyList<string>? LastArguments { get; private set; }

        public TimeSpan? LastTimeout { get; private set; }

        public static FakeRunner Succeeding(string standardOutput) =>
            new(_ => new AutoModeProbeOutput(0, standardOutput, string.Empty));

        public static FakeRunner Failing(int exitCode, string standardError) =>
            new(_ => new AutoModeProbeOutput(exitCode, string.Empty, standardError));

        public static FakeRunner Throwing(Exception exception) =>
            new(_ => throw exception);

        public Task<AutoModeProbeOutput> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastExecutable = executable;
            LastArguments = arguments;
            LastTimeout = timeout;
            return Task.FromResult(respond(arguments));
        }
    }

    sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
