using NightShift.Core.Configuration;
using NightShift.Core.Usage;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace NightShift.Core.Tests.Usage;

/// <summary>
/// Parse-path coverage for the ccusage fallback (plan.md §4.2). Every case drives the provider
/// through a fake process runner, so nothing here ever spawns <c>npx</c>.
/// </summary>
public sealed class CcusageProviderTests
{
    static readonly DateTimeOffset Now = new(2026, 7, 26, 15, 0, 0, TimeSpan.Zero);

    static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    static CcusageProvider Provider(
        ICcusageProcessRunner runner,
        string commandOverride = "")
    {
        var settings = new PilotSettings { CcusageCommandOverride = commandOverride };
        var store = Substitute.For<ISettingsStore>();
        store.Current.Returns(settings);

        return new CcusageProvider(
            store,
            runner,
            NullLogger<CcusageProvider>.Instance,
            new FixedTimeProvider(Now));
    }

    static Task<UsageSnapshot> RunAsync(FakeRunner runner, string commandOverride = "") =>
        Provider(runner, commandOverride).GetUsageAsync(cancellationToken: CancellationToken.None);

    // ── Envelope shapes ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_blocks_envelope_with_nested_token_counts_is_parsed()
    {
        var runner = FakeRunner.Succeeding(Fixture("ccusage-blocks-envelope-nested-tokens.json"));

        var snapshot = await RunAsync(runner);

        Assert.True(snapshot.IsAvailable);
        Assert.Equal(25d, snapshot.FiveHour!.UtilizationPercent, precision: 6);
        Assert.Equal(new DateTimeOffset(2026, 7, 26, 17, 0, 0, TimeSpan.Zero), snapshot.FiveHour.ResetsAt);
        Assert.Equal(Now, snapshot.RetrievedAt);
    }

    [Fact]
    public async Task The_data_and_summary_envelope_with_flat_token_fields_is_parsed()
    {
        var runner = FakeRunner.Succeeding(Fixture("ccusage-data-envelope-flat-tokens.json"));

        var snapshot = await RunAsync(runner);

        Assert.Equal(50d, snapshot.FiveHour!.UtilizationPercent, precision: 6);
        Assert.Equal(new DateTimeOffset(2026, 7, 26, 18, 30, 0, TimeSpan.Zero), snapshot.FiveHour.ResetsAt);
    }

    [Fact]
    public async Task The_blocks_envelope_with_flat_token_fields_is_parsed()
    {
        var runner = FakeRunner.Succeeding(Fixture("ccusage-blocks-envelope-flat-tokens.json"));

        var snapshot = await RunAsync(runner);

        // The inactive block sits first in the array — the active one must still be the one read.
        Assert.Equal(15d, snapshot.FiveHour!.UtilizationPercent, precision: 6);
        Assert.Equal(new DateTimeOffset(2026, 7, 26, 16, 15, 0, TimeSpan.Zero), snapshot.FiveHour.ResetsAt);
    }

    [Fact]
    public async Task The_data_and_summary_envelope_with_nested_token_counts_is_parsed()
    {
        var runner = FakeRunner.Succeeding(Fixture("ccusage-data-envelope-nested-tokens.json"));

        var snapshot = await RunAsync(runner);

        Assert.Equal(80d, snapshot.FiveHour!.UtilizationPercent, precision: 6);
    }

    // ── Captured from a real ccusage 20.0.18 run on 2026-07-26 ────────────────────────────────

    [Fact]
    public async Task The_real_payload_is_parsed_to_the_percentage_ccusage_itself_reports()
    {
        var runner = FakeRunner.Succeeding(Fixture("ccusage-real-active-block-with-limit-v20.json"));

        var snapshot = await RunAsync(runner);

        Assert.Equal(38.76798715541708d, snapshot.FiveHour!.UtilizationPercent, precision: 6);
        Assert.Equal(new DateTimeOffset(2026, 7, 27, 1, 0, 0, TimeSpan.Zero), snapshot.FiveHour.ResetsAt);
        Assert.True(snapshot.IsApproximate);
    }

    [Fact]
    public async Task A_reported_percentage_wins_over_recomputing_it_from_the_token_counts()
    {
        var runner = FakeRunner.Succeeding(Fixture("ccusage-real-active-block-with-limit-v20.json"));

        var snapshot = await RunAsync(runner);

        // 19_352_508 / 448_607_482 is ~4.3%: ccusage's own figure accounts for projected usage, and
        // the two must not disagree with what the user sees in `ccusage blocks`.
        var computed = 19_352_508d / 448_607_482d * 100d;
        Assert.NotEqual(computed, snapshot.FiveHour!.UtilizationPercent, precision: 3);
        Assert.Equal(38.76798715541708d, snapshot.FiveHour.UtilizationPercent, precision: 6);
    }

    [Fact]
    public async Task The_real_payload_from_an_active_only_run_has_no_limit_and_is_unavailable()
    {
        // Regression guard for the `--active` trap: with `--token-limit max` ccusage needs the full
        // block history, and filtering to the active block makes it drop tokenLimitStatus silently.
        var runner = FakeRunner.Succeeding(Fixture("ccusage-real-active-block-v20.json"));

        var snapshot = await RunAsync(runner);

        AssertUnavailable(snapshot, "token limit");
    }

    // ── Snapshot shape ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Snapshots_are_marked_approximate_and_sourced_to_ccusage()
    {
        var runner = FakeRunner.Succeeding(Fixture("ccusage-blocks-envelope-nested-tokens.json"));

        var snapshot = await RunAsync(runner);

        Assert.Equal(UsageSource.Ccusage, snapshot.Source);
        Assert.True(snapshot.IsApproximate);
        Assert.Null(snapshot.UnavailableReason);
    }

    [Fact]
    public async Task Only_the_five_hour_window_is_populated()
    {
        var runner = FakeRunner.Succeeding(Fixture("ccusage-blocks-envelope-nested-tokens.json"));

        var snapshot = await RunAsync(runner);

        // ccusage measures the rolling 5-hour block and nothing else (plan.md §4.2).
        Assert.NotNull(snapshot.FiveHour);
        Assert.Null(snapshot.SevenDay);
        Assert.Null(snapshot.SevenDayOpus);
        Assert.Null(snapshot.SevenDaySonnet);
    }

    [Fact]
    public async Task A_block_past_its_limit_is_clamped_to_one_hundred_percent()
    {
        var runner = FakeRunner.Succeeding(Fixture("ccusage-over-limit-block.json"));

        var snapshot = await RunAsync(runner);

        Assert.Equal(100d, snapshot.FiveHour!.UtilizationPercent, precision: 6);
    }

    [Fact]
    public async Task Npx_noise_around_the_json_is_tolerated()
    {
        var runner = FakeRunner.Succeeding(
            "Need to install the following packages:\nccusage@latest\n" +
            Fixture("ccusage-blocks-envelope-nested-tokens.json") +
            "\ndone in 4.2s\n");

        var snapshot = await RunAsync(runner);

        Assert.Equal(25d, snapshot.FiveHour!.UtilizationPercent, precision: 6);
    }

    // ── Failure paths — none of these may throw ────────────────────────────────────────────────

    [Fact]
    public async Task No_active_block_is_unavailable()
    {
        var runner = FakeRunner.Succeeding(Fixture("ccusage-no-active-block.json"));

        var snapshot = await RunAsync(runner);

        AssertUnavailable(snapshot, "no active");
    }

    [Fact]
    public async Task A_missing_token_limit_is_unavailable_rather_than_an_invented_denominator()
    {
        var runner = FakeRunner.Succeeding(Fixture("ccusage-missing-token-limit.json"));

        var snapshot = await RunAsync(runner);

        AssertUnavailable(snapshot, "token limit");
    }

    [Fact]
    public async Task Empty_output_is_unavailable()
    {
        var snapshot = await RunAsync(FakeRunner.Succeeding("   \n"));

        AssertUnavailable(snapshot, "no JSON");
    }

    [Fact]
    public async Task Invalid_json_is_unavailable()
    {
        var snapshot = await RunAsync(FakeRunner.Succeeding("{ \"blocks\": [ this is not json ]"));

        AssertUnavailable(snapshot, "not valid JSON");
    }

    [Fact]
    public async Task A_non_zero_exit_code_is_unavailable_and_reports_the_first_stderr_line()
    {
        var runner = FakeRunner.Failing(exitCode: 1, standardError: "command not found: ccusage\nstack…");

        var snapshot = await RunAsync(runner);

        AssertUnavailable(snapshot, "command not found: ccusage");
        Assert.DoesNotContain("stack", snapshot.UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_program_that_cannot_be_started_is_unavailable()
    {
        var runner = FakeRunner.Throwing(
            new System.ComponentModel.Win32Exception("The system cannot find the file specified."));

        var snapshot = await RunAsync(runner);

        AssertUnavailable(snapshot, "could not be run");
    }

    [Fact]
    public async Task A_timeout_is_unavailable()
    {
        var runner = new FakeRunner(_ => new CcusageResult(-1, string.Empty, string.Empty, TimedOut: true));

        var snapshot = await RunAsync(runner);

        AssertUnavailable(snapshot, "did not finish");
        Assert.Equal(CcusageProvider.DefaultTimeout, runner.LastTimeout);
    }

    // ── Command resolution ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_default_command_is_used_when_no_override_is_set()
    {
        var runner = FakeRunner.Succeeding(Fixture("ccusage-blocks-envelope-nested-tokens.json"));

        await RunAsync(runner);

        Assert.Equal(CcusageProvider.DefaultCommand, runner.LastCommand!.ToString());
        Assert.Equal("npx", runner.LastCommand.FileName);
        Assert.Contains("--token-limit", runner.LastCommand.Arguments);
        Assert.Contains("max", runner.LastCommand.Arguments);

        // `--active` would leave `max` with no history to resolve against, and ccusage answers that
        // by omitting the limit rather than failing — which disables this provider entirely.
        Assert.DoesNotContain("--active", runner.LastCommand.Arguments);
    }

    [Fact]
    public async Task The_command_override_is_honoured_including_quoted_paths()
    {
        var runner = FakeRunner.Succeeding(Fixture("ccusage-blocks-envelope-nested-tokens.json"));

        await RunAsync(runner, "\"C:\\Program Files\\nodejs\\ccusage.cmd\" blocks --active --json --token-limit 500000");

        Assert.Equal(@"C:\Program Files\nodejs\ccusage.cmd", runner.LastCommand!.FileName);
        Assert.Equal(
            ["blocks", "--active", "--json", "--token-limit", "500000"],
            runner.LastCommand.Arguments);
    }

    [Fact]
    public async Task A_whitespace_only_override_falls_back_to_the_default_command()
    {
        var runner = FakeRunner.Succeeding(Fixture("ccusage-blocks-envelope-nested-tokens.json"));

        await RunAsync(runner, "   ");

        Assert.Equal(CcusageProvider.DefaultCommand, runner.LastCommand!.ToString());
    }

    static void AssertUnavailable(UsageSnapshot snapshot, string expectedInReason)
    {
        Assert.False(snapshot.IsAvailable);
        Assert.Equal(UsageSource.None, snapshot.Source);
        Assert.Null(snapshot.FiveHour);
        Assert.NotNull(snapshot.UnavailableReason);
        Assert.Contains(expectedInReason, snapshot.UnavailableReason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Records what was asked for and replays a canned result. Never touches a process.</summary>
    sealed class FakeRunner(Func<CcusageCommand, CcusageResult> respond) : ICcusageProcessRunner
    {
        public CcusageCommand? LastCommand { get; private set; }

        public TimeSpan? LastTimeout { get; private set; }

        public static FakeRunner Succeeding(string standardOutput) =>
            new(_ => new CcusageResult(0, standardOutput, string.Empty));

        public static FakeRunner Failing(int exitCode, string standardError) =>
            new(_ => new CcusageResult(exitCode, string.Empty, standardError));

        public static FakeRunner Throwing(Exception exception) =>
            new(_ => throw exception);

        public Task<CcusageResult> RunAsync(
            CcusageCommand command,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            LastCommand = command;
            LastTimeout = timeout;
            return Task.FromResult(respond(command));
        }
    }

    sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
