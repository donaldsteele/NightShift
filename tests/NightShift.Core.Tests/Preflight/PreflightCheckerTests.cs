using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NightShift.Core.Configuration;
using NightShift.Core.Execution;
using NightShift.Core.Preflight;
using NightShift.Core.Usage;
using NSubstitute;

namespace NightShift.Core.Tests.Preflight;

/// <summary>
/// plan.md §7. Every dependency is injected and every path points into a temp directory: these tests
/// never spawn <c>claude</c>, never read the developer's <c>~/.claude</c>, and never shell out to git
/// (<see cref="It_never_starts_a_process_other_than_the_fake_cli_and_git"/> pins the first of those).
/// </summary>
public sealed class PreflightCheckerTests : IDisposable
{
    readonly Harness _harness = new();

    public void Dispose() => _harness.Dispose();

    // ── Plan checkbox parsing (plan.md §9.1) ───────────────────────────────────────────────────

    [Fact]
    public void Checkboxes_are_counted_by_mark()
    {
        var counts = PreflightChecker.CountPlanItems("""
            # Plan

            - [x] Done
            - [x] Also done
            - [ ] Still to do
            - [!] Blocked on something
            Not a checkbox at all.
            """);

        Assert.Equal(2, counts.Completed);
        Assert.Equal(1, counts.Remaining);
        Assert.Equal(1, counts.Blocked);
        Assert.Equal(4, counts.Total);
        Assert.True(counts.HasRemainingWork);
        Assert.Equal("2 of 4 items complete", counts.Summary);
    }

    [Fact]
    public void Indented_and_star_bulleted_checkboxes_count_too()
    {
        var counts = PreflightChecker.CountPlanItems("- [ ] a\n    - [x] b\n* [ ] c\n+ [!] d\n");

        Assert.Equal(1, counts.Completed);
        Assert.Equal(2, counts.Remaining);
        Assert.Equal(1, counts.Blocked);
    }

    [Fact]
    public void Checkboxes_inside_a_fenced_code_block_are_ignored()
    {
        var counts = PreflightChecker.CountPlanItems("""
            - [ ] Real work

            Example of the convention:

            ```
            - [x] not a real item
            - [!] not a real item either
            ```

            - [x] Real, and done
            """);

        Assert.Equal(1, counts.Completed);
        Assert.Equal(1, counts.Remaining);
        Assert.Equal(0, counts.Blocked);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("# A plan with prose only.")]
    public void A_plan_with_no_checkboxes_counts_zero(string? text)
    {
        var counts = PreflightChecker.CountPlanItems(text);

        Assert.Equal(0, counts.Total);
        Assert.False(counts.HasRemainingWork);
    }

    // ── The all-green run ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_healthy_machine_produces_every_row_green()
    {
        var result = await _harness.CheckAsync();

        Assert.Equal(PreflightStatus.Ok, result.Status);
        Assert.True(result.CanRun);
        Assert.False(result.HasErrors);
        Assert.Equal("Ready.", result.Summary);
        Assert.All(result.Checks, check => Assert.Equal(PreflightStatus.Ok, check.Status));
        Assert.All(result.Checks, check => Assert.NotEmpty(check.Message));
    }

    [Fact]
    public async Task Every_check_in_the_table_is_reported_exactly_once_in_order()
    {
        var result = await _harness.CheckAsync();

        Assert.Equal(
            [
                PreflightCheckId.ClaudeExecutable,
                PreflightCheckId.ClaudeVersion,
                PreflightCheckId.ProjectDirectory,
                PreflightCheckId.PlanFile,
                PreflightCheckId.PlanItems,
                PreflightCheckId.Credentials,
                PreflightCheckId.CavemanPlugin,
                PreflightCheckId.WorkspaceTrust,
                PreflightCheckId.AutoMode,
                PreflightCheckId.PermissionsAsk,
                PreflightCheckId.GitRepository,
                PreflightCheckId.GitWorkingTree,
            ],
            result.Checks.Select(check => check.Id).ToArray());
    }

    [Fact]
    public async Task The_result_carries_the_executable_the_counts_and_the_permission_decision()
    {
        var result = await _harness.CheckAsync();

        Assert.Equal(_harness.ClaudeExecutable, result.ClaudeExecutablePath);
        Assert.Equal(1, result.PlanItems?.Remaining);
        Assert.Equal(PermissionMode.Auto, result.PermissionMode?.Effective);
        Assert.Equal(_harness.Clock.GetUtcNow(), result.CheckedAt);
    }

    [Fact]
    public async Task It_never_starts_a_process_other_than_the_fake_cli_and_git()
    {
        await _harness.CheckAsync();

        Assert.NotEmpty(_harness.Runner.Calls);
        Assert.All(
            _harness.Runner.Calls,
            call => Assert.True(
                call.Executable == _harness.ClaudeExecutable || call.Executable == PreflightChecker.GitExecutable,
                $"Unexpected process: {call.Executable}"));
    }

    // ── Claude CLI ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_missing_cli_is_an_error_that_lists_the_probed_paths()
    {
        _harness.Locator
            .Locate(Arg.Any<PilotSettings>())
            .Returns(new ClaudeExecutableResolution(
                null, ClaudeExecutableSource.NotFound, "nope", ["C:/one/claude.cmd", "C:/two/claude.exe"]));

        var check = await _harness.CheckAsync(PreflightCheckId.ClaudeExecutable);

        Assert.Equal(PreflightStatus.Error, check.Status);
        Assert.Equal(["C:/one/claude.cmd", "C:/two/claude.exe"], check.Details);
        Assert.Equal(PreflightFixCommand.SetClaudeExecutablePath, check.Fix?.Command);
    }

    [Fact]
    public async Task With_no_cli_the_version_check_is_an_error_and_no_process_is_started()
    {
        _harness.Locator
            .Locate(Arg.Any<PilotSettings>())
            .Returns(new ClaudeExecutableResolution(null, ClaudeExecutableSource.NotFound, "nope", []));

        var check = await _harness.CheckAsync(PreflightCheckId.ClaudeVersion);

        Assert.Equal(PreflightStatus.Error, check.Status);
        Assert.Null(check.Fix);
        Assert.DoesNotContain(_harness.Runner.Calls, call => call.Arguments == "--version");
    }

    [Fact]
    public async Task A_cli_that_reports_its_version_is_green_and_the_version_is_shown()
    {
        var check = await _harness.CheckAsync(PreflightCheckId.ClaudeVersion);

        Assert.Equal(PreflightStatus.Ok, check.Status);
        Assert.Contains("2.1.220", check.Message);
    }

    [Fact]
    public async Task A_cli_that_will_not_start_is_an_error()
    {
        _harness.Runner.Responses["--version"] =
            new PreflightProcessResult(-1, string.Empty, "not executable", Started: false);

        var check = await _harness.CheckAsync(PreflightCheckId.ClaudeVersion);

        Assert.Equal(PreflightStatus.Error, check.Status);
        Assert.Contains("failed to start", check.Message);
    }

    [Fact]
    public async Task A_cli_that_hangs_is_an_error()
    {
        _harness.Runner.Responses["--version"] =
            new PreflightProcessResult(-1, string.Empty, string.Empty, TimedOut: true);

        var check = await _harness.CheckAsync(PreflightCheckId.ClaudeVersion);

        Assert.Equal(PreflightStatus.Error, check.Status);
        Assert.Contains("did not finish", check.Message);
    }

    [Fact]
    public async Task A_cli_that_exits_non_zero_is_an_error_naming_the_code()
    {
        _harness.Runner.Responses["--version"] = new PreflightProcessResult(9, string.Empty, "boom");

        var check = await _harness.CheckAsync(PreflightCheckId.ClaudeVersion);

        Assert.Equal(PreflightStatus.Error, check.Status);
        Assert.Contains("9", check.Message);
        Assert.Contains("boom", check.Message);
    }

    // ── Project directory and plan file ────────────────────────────────────────────────────────

    [Fact]
    public async Task An_unset_project_directory_is_an_error_with_a_picker_fix()
    {
        _harness.Settings = _harness.Settings with { ProjectDirectory = string.Empty };

        var check = await _harness.CheckAsync(PreflightCheckId.ProjectDirectory);

        Assert.Equal(PreflightStatus.Error, check.Status);
        Assert.Equal(PreflightFixCommand.SetProjectDirectory, check.Fix?.Command);
    }

    [Fact]
    public async Task A_missing_project_directory_is_an_error_and_the_dependent_rows_do_not_throw()
    {
        _harness.Settings = _harness.Settings with
        {
            ProjectDirectory = Path.Combine(_harness.Root, "gone"),
        };

        var result = await _harness.CheckAsync();

        Assert.Equal(PreflightStatus.Error, result.Find(PreflightCheckId.ProjectDirectory)!.Status);
        Assert.Equal(PreflightStatus.Error, result.Find(PreflightCheckId.PlanFile)!.Status);
        Assert.Equal(PreflightStatus.Warning, result.Find(PreflightCheckId.GitRepository)!.Status);
        Assert.Equal(PreflightStatus.Warning, result.Find(PreflightCheckId.WorkspaceTrust)!.Status);
        Assert.Null(result.PlanItems);
    }

    [Fact]
    public async Task A_missing_plan_file_is_an_error_offering_to_create_a_starter()
    {
        File.Delete(_harness.PlanPath);

        var check = await _harness.CheckAsync(PreflightCheckId.PlanFile);

        Assert.Equal(PreflightStatus.Error, check.Status);
        Assert.Equal(PreflightFixCommand.CreatePlanFile, check.Fix?.Command);
        Assert.Equal(_harness.PlanPath, check.Fix?.Target);
    }

    [Fact]
    public async Task The_configured_plan_file_name_is_used_rather_than_a_hardcoded_plan_md()
    {
        // plan.md exists; tasks.md does not. A hardcoded "plan.md" would report this as green.
        _harness.Settings = _harness.Settings with { PlanFileName = "tasks.md" };

        var check = await _harness.CheckAsync(PreflightCheckId.PlanFile);

        Assert.Equal(PreflightStatus.Error, check.Status);
        Assert.Contains("tasks.md", check.Message);
    }

    [Fact]
    public async Task A_plan_with_nothing_left_to_tick_is_a_warning()
    {
        File.WriteAllText(_harness.PlanPath, "- [x] Done\n- [x] Also done\n");

        var result = await _harness.CheckAsync();
        var check = result.Find(PreflightCheckId.PlanItems)!;

        Assert.Equal(PreflightStatus.Warning, check.Status);
        Assert.Contains("2 of 2 items complete", check.Message);
        Assert.True(result.CanRun, "A finished plan is a warning, not a blocker.");
    }

    [Fact]
    public async Task A_plan_whose_only_remaining_items_are_blocked_is_a_warning_that_says_so()
    {
        File.WriteAllText(_harness.PlanPath, "- [x] Done\n- [!] Stuck on an API key\n");

        var check = await _harness.CheckAsync(PreflightCheckId.PlanItems);

        Assert.Equal(PreflightStatus.Warning, check.Status);
        Assert.Contains("blocked", check.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_plan_with_no_checkboxes_at_all_is_a_warning()
    {
        File.WriteAllText(_harness.PlanPath, "# Just prose.\n");

        var check = await _harness.CheckAsync(PreflightCheckId.PlanItems);

        Assert.Equal(PreflightStatus.Warning, check.Status);
        Assert.Equal(PreflightFixCommand.OpenPlanFile, check.Fix?.Command);
    }

    [Fact]
    public async Task A_milestone_plan_is_detected_counted_and_carried_on_the_result()
    {
        File.WriteAllText(
            _harness.PlanPath,
            """
            ### M0 — scaffold

            ### M1 — the hard part — **delivered 2026-07-26**
            **Status:** implemented.

            ### M2 — the rest
            """);

        var result = await _harness.CheckAsync();
        var check = result.Find(PreflightCheckId.PlanItems)!;

        Assert.Equal(PlanFormat.Milestone, result.PlanFormat);
        Assert.Equal(2, result.PlanItems?.Completed);      // M0 backfilled behind the delivered M1
        Assert.Equal(1, result.PlanItems?.Remaining);
        Assert.Equal(PreflightStatus.Ok, check.Status);
        Assert.Contains("1 milestone left to do (2 of 3 milestones complete)", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_milestone_plan_with_everything_delivered_says_so_in_its_own_words()
    {
        File.WriteAllText(_harness.PlanPath, "### M0 — done — **delivered 2026-07-26**\n");

        var check = await _harness.CheckAsync(PreflightCheckId.PlanItems);

        Assert.Equal(PreflightStatus.Warning, check.Status);
        Assert.Contains("already delivered", check.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("checkbox", check.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_checkbox_plan_still_reads_exactly_as_it_did()
    {
        // The regression that matters most: this repo's own plan.md must be unaffected by milestones.
        File.WriteAllText(_harness.PlanPath, "## Phase 0\n- [x] a\n- [ ] b\n");

        var result = await _harness.CheckAsync();

        Assert.Equal(PlanFormat.Checkbox, result.PlanFormat);
        Assert.Contains("1 item left to do (1 of 2 items complete)", result.Find(PreflightCheckId.PlanItems)!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_counts_are_exposed_for_the_dashboard_card()
    {
        File.WriteAllText(_harness.PlanPath, "- [x] a\n- [x] b\n- [ ] c\n- [!] d\n");

        var result = await _harness.CheckAsync();

        Assert.Equal(new PlanItemCounts(2, 1, 1), result.PlanItems);
        Assert.Equal("2 of 4 items complete", result.PlanItems!.Summary);
    }

    // ── Credentials ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task No_login_is_an_error_offering_the_login_instructions()
    {
        _harness.Credentials.ReadAsync(Arg.Any<CancellationToken>()).Returns((ClaudeCredentials?)null);

        var check = await _harness.CheckAsync(PreflightCheckId.Credentials);

        Assert.Equal(PreflightStatus.Error, check.Status);
        Assert.Equal(PreflightFixCommand.ShowLoginInstructions, check.Fix?.Command);
    }

    [Fact]
    public async Task An_expired_login_is_an_error_judged_against_the_injected_clock()
    {
        _harness.Credentials
            .ReadAsync(Arg.Any<CancellationToken>())
            .Returns(new ClaudeCredentials("sk-ant-oat01-SECRET", _harness.Clock.GetUtcNow().AddMinutes(-1)));

        var check = await _harness.CheckAsync(PreflightCheckId.Credentials);

        Assert.Equal(PreflightStatus.Error, check.Status);
        Assert.Contains("expired", check.Message);
    }

    [Fact]
    public async Task The_access_token_never_appears_anywhere_in_the_result()
    {
        const string token = "sk-ant-oat01-DO-NOT-LEAK-ME";
        _harness.Credentials
            .ReadAsync(Arg.Any<CancellationToken>())
            .Returns(new ClaudeCredentials(token, _harness.Clock.GetUtcNow().AddHours(1)));

        var result = await _harness.CheckAsync();

        foreach (var check in result.Checks)
        {
            Assert.DoesNotContain(token, check.Message, StringComparison.Ordinal);
            Assert.All(check.Details, detail => Assert.DoesNotContain(token, detail, StringComparison.Ordinal));
        }

        Assert.DoesNotContain(token, result.Summary, StringComparison.Ordinal);
    }

    // ── caveman (plan.md §5.2 correction) ──────────────────────────────────────────────────────

    [Fact]
    public async Task An_installed_plugin_that_provides_the_command_is_green()
    {
        var check = await _harness.CheckAsync(PreflightCheckId.CavemanPlugin);

        Assert.Equal(PreflightStatus.Ok, check.Status);
        Assert.Contains("/caveman:caveman", check.Message);
    }

    [Fact]
    public async Task A_plugin_installed_without_a_caveman_command_is_an_error()
    {
        // The §5.2 trap: the plugin is there, but `/caveman:caveman` would not resolve, so the whole
        // prompt is swallowed and the run is recorded as a success having done nothing.
        File.Delete(Path.Combine(_harness.CavemanInstallPath, "commands", "caveman.md"));

        var check = await _harness.CheckAsync(PreflightCheckId.CavemanPlugin);

        Assert.Equal(PreflightStatus.Error, check.Status);
        Assert.Contains("/caveman:caveman", check.Message);
        Assert.Equal(PreflightFixCommand.InstallCavemanPlugin, check.Fix?.Command);
    }

    [Fact]
    public async Task A_bare_caveman_command_does_not_satisfy_the_check()
    {
        // A plugin whose only command is `caveman-review` still leaves `/caveman:caveman` unknown.
        var commands = Path.Combine(_harness.CavemanInstallPath, "commands");
        File.Delete(Path.Combine(commands, "caveman.md"));
        File.WriteAllText(Path.Combine(commands, "caveman-review.md"), "# review");

        var check = await _harness.CheckAsync(PreflightCheckId.CavemanPlugin);

        Assert.Equal(PreflightStatus.Error, check.Status);
    }

    [Fact]
    public async Task A_manifest_without_caveman_is_an_error_offering_the_install()
    {
        _harness.WritePluginManifest("""{ "version": 2, "plugins": { "llm-wiki@llm-wiki": [] } }""");

        var check = await _harness.CheckAsync(PreflightCheckId.CavemanPlugin);

        Assert.Equal(PreflightStatus.Error, check.Status);
        Assert.Equal(PreflightFixCommand.InstallCavemanPlugin, check.Fix?.Command);
    }

    [Fact]
    public async Task With_no_manifest_the_plan_md_directory_probe_is_the_fallback()
    {
        File.Delete(_harness.PluginManifestPath);
        Directory.CreateDirectory(Path.Combine(_harness.PluginsDirectory, "caveman"));

        var check = await _harness.CheckAsync(PreflightCheckId.CavemanPlugin);

        Assert.Equal(PreflightStatus.Ok, check.Status);
    }

    [Fact]
    public async Task With_no_plugins_directory_the_cli_answer_is_used()
    {
        Directory.Delete(_harness.PluginsDirectory, recursive: true);
        _harness.Runner.Responses["plugin list"] = new PreflightProcessResult(0, "caveman@caveman  0d95a81", "");

        var check = await _harness.CheckAsync(PreflightCheckId.CavemanPlugin);

        Assert.Equal(PreflightStatus.Ok, check.Status);
    }

    [Fact]
    public async Task When_neither_source_can_answer_it_is_a_warning_not_an_error()
    {
        Directory.Delete(_harness.PluginsDirectory, recursive: true);
        _harness.Runner.Responses["plugin list"] =
            new PreflightProcessResult(-1, string.Empty, string.Empty, Started: false);

        var check = await _harness.CheckAsync(PreflightCheckId.CavemanPlugin);

        Assert.Equal(PreflightStatus.Warning, check.Status);
    }

    [Fact]
    public async Task A_cli_that_lists_no_caveman_is_an_error()
    {
        Directory.Delete(_harness.PluginsDirectory, recursive: true);
        _harness.Runner.Responses["plugin list"] = new PreflightProcessResult(0, "llm-wiki@llm-wiki", "");

        var check = await _harness.CheckAsync(PreflightCheckId.CavemanPlugin);

        Assert.Equal(PreflightStatus.Error, check.Status);
    }

    // ── Workspace trust (plan.md §5.3.1 correction) ────────────────────────────────────────────

    [Fact]
    public async Task An_untrusted_folder_is_a_warning_and_never_blocks_a_run()
    {
        File.WriteAllText(_harness.ClaudeConfigPath, "{}");

        var result = await _harness.CheckAsync();
        var check = result.Find(PreflightCheckId.WorkspaceTrust)!;

        Assert.Equal(PreflightStatus.Warning, check.Status);
        Assert.True(result.CanRun, "Headless runs proceed without the trust flags (measured on 2.1.220).");
        Assert.Equal(PreflightFixCommand.TrustProjectFolder, check.Fix?.Command);
        Assert.NotEmpty(check.Details);
    }

    [Fact]
    public async Task A_missing_claude_json_is_a_warning_that_says_why()
    {
        File.Delete(_harness.ClaudeConfigPath);

        var check = await _harness.CheckAsync(PreflightCheckId.WorkspaceTrust);

        Assert.Equal(PreflightStatus.Warning, check.Status);
        Assert.Contains("Could not confirm", check.Message);
    }

    // ── Auto mode ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Auto_mode_unavailable_is_a_warning_that_explains_the_downgrade()
    {
        _harness.AutoModeUnavailable();

        var result = await _harness.CheckAsync();
        var check = result.Find(PreflightCheckId.AutoMode)!;

        Assert.Equal(PreflightStatus.Warning, check.Status);
        Assert.Contains("acceptEdits", check.Message);
        Assert.True(result.CanRun);
        Assert.Equal(PermissionMode.AcceptEdits, result.PermissionMode?.Effective);
        Assert.Contains("--permission-mode acceptEdits", check.Details);
    }

    [Fact]
    public async Task A_deliberately_configured_mode_needs_no_probe_and_is_green()
    {
        _harness.Settings = _harness.Settings with { PermissionMode = PermissionMode.AcceptEdits };

        var check = await _harness.CheckAsync(PreflightCheckId.AutoMode);

        Assert.Equal(PreflightStatus.Ok, check.Status);
        Assert.Contains("acceptEdits", check.Message);
        await _harness.AutoModeRunner.DidNotReceiveWithAnyArgs().RunAsync(default!, default!, default, default);
    }

    // ── permissions.ask (plan.md §5.3.2) ───────────────────────────────────────────────────────

    [Fact]
    public async Task An_ask_rule_is_an_error_that_lists_the_rules_and_blocks_the_run()
    {
        _harness.WriteUserSettings("""{ "permissions": { "ask": ["Bash(git push:*)", "WebFetch"] } }""");

        var result = await _harness.CheckAsync();
        var check = result.Find(PreflightCheckId.PermissionsAsk)!;

        Assert.Equal(PreflightStatus.Error, check.Status);
        Assert.Contains("Bash(git push:*)", check.Message);
        Assert.Contains("WebFetch", check.Message);
        Assert.Equal(2, check.Details.Count);
        Assert.Equal(PreflightFixCommand.OpenSettingsFile, check.Fix?.Command);
        Assert.Equal(_harness.UserSettingsPath, check.Fix?.Target);
        Assert.False(result.CanRun);
    }

    [Fact]
    public async Task An_interactive_mcp_server_is_also_an_error()
    {
        _harness.WriteUserSettings("""{ "mcpServers": { "loud": { "requiresUserInteraction": true } } }""");

        var check = await _harness.CheckAsync(PreflightCheckId.PermissionsAsk);

        Assert.Equal(PreflightStatus.Error, check.Status);
        Assert.Contains("loud", check.Message);
    }

    [Fact]
    public async Task A_settings_file_that_cannot_be_parsed_is_only_a_warning()
    {
        _harness.WriteUserSettings("{ not json");

        var result = await _harness.CheckAsync();
        var check = result.Find(PreflightCheckId.PermissionsAsk)!;

        Assert.Equal(PreflightStatus.Warning, check.Status);
        Assert.True(result.CanRun);
    }

    // ── Git (warning only, always) ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Git_runs_in_the_project_directory()
    {
        await _harness.CheckAsync();

        var call = Assert.Single(
            _harness.Runner.Calls, c => c.Arguments == "rev-parse --is-inside-work-tree");
        Assert.Equal(PreflightChecker.GitExecutable, call.Executable);
        Assert.Equal(_harness.ProjectDirectory, call.WorkingDirectory);
    }

    [Fact]
    public async Task A_directory_that_is_not_a_repository_is_a_warning_offering_git_init()
    {
        _harness.Runner.Responses["rev-parse --is-inside-work-tree"] =
            new PreflightProcessResult(128, string.Empty, "fatal: not a git repository");

        var result = await _harness.CheckAsync();

        var repository = result.Find(PreflightCheckId.GitRepository)!;
        Assert.Equal(PreflightStatus.Warning, repository.Status);
        Assert.Equal(PreflightFixCommand.InitializeGitRepository, repository.Fix?.Command);
        Assert.Equal(PreflightStatus.Warning, result.Find(PreflightCheckId.GitWorkingTree)!.Status);
        Assert.True(result.CanRun);
        Assert.DoesNotContain(_harness.Runner.Calls, call => call.Arguments == "status --porcelain");
    }

    [Fact]
    public async Task Git_not_being_installed_is_a_warning_never_an_exception()
    {
        _harness.Runner.Responses["rev-parse --is-inside-work-tree"] =
            new PreflightProcessResult(-1, string.Empty, string.Empty, Started: false);

        var result = await _harness.CheckAsync();

        Assert.Equal(PreflightStatus.Warning, result.Find(PreflightCheckId.GitRepository)!.Status);
        Assert.Contains("git is not installed", result.Find(PreflightCheckId.GitRepository)!.Message);
        Assert.Equal(PreflightStatus.Warning, result.Find(PreflightCheckId.GitWorkingTree)!.Status);
        Assert.True(result.CanRun);
    }

    [Fact]
    public async Task A_process_runner_that_throws_still_only_warns_for_git()
    {
        _harness.Runner.Throw = new InvalidOperationException("the runner exploded");

        var result = await _harness.CheckAsync();

        Assert.Equal(PreflightStatus.Warning, result.Find(PreflightCheckId.GitRepository)!.Status);
        Assert.Equal(PreflightStatus.Warning, result.Find(PreflightCheckId.GitWorkingTree)!.Status);
    }

    [Fact]
    public async Task Uncommitted_changes_are_a_warning_that_counts_and_lists_them()
    {
        _harness.Runner.Responses["status --porcelain"] =
            new PreflightProcessResult(0, " M src/a.cs\n?? src/b.cs\n", string.Empty);

        var result = await _harness.CheckAsync();
        var check = result.Find(PreflightCheckId.GitWorkingTree)!;

        Assert.Equal(PreflightStatus.Warning, check.Status);
        Assert.Contains("2 uncommitted changes", check.Message);
        Assert.Equal(["M src/a.cs", "?? src/b.cs"], check.Details);
        Assert.True(result.CanRun);
    }

    // ── Nothing may throw out of preflight ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_check_that_throws_becomes_a_red_row_rather_than_taking_the_scheduler_down()
    {
        _harness.Locator
            .Locate(Arg.Any<PilotSettings>())
            .Returns(_ => throw new InvalidOperationException("the locator exploded"));
        _harness.Credentials
            .ReadAsync(Arg.Any<CancellationToken>())
            .Returns<Task<ClaudeCredentials?>>(_ => throw new IOException("the credential store exploded"));

        var result = await _harness.CheckAsync();

        Assert.Equal(PreflightStatus.Error, result.Find(PreflightCheckId.ClaudeExecutable)!.Status);
        Assert.Contains("locator exploded", result.Find(PreflightCheckId.ClaudeExecutable)!.Message);
        Assert.Equal(PreflightStatus.Error, result.Find(PreflightCheckId.Credentials)!.Status);
        Assert.Contains("credential store exploded", result.Find(PreflightCheckId.Credentials)!.Message);
        Assert.Null(result.ClaudeExecutablePath);
        Assert.Equal(12, result.Checks.Count);
    }

    [Fact]
    public async Task A_cancelled_preflight_still_cancels()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _harness.Create().CheckAsync(_harness.Settings, cancellation.Token));
    }

    [Fact]
    public async Task Null_settings_are_rejected()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _harness.Create().CheckAsync(null!));
    }

    // ── The result shape the scheduler consumes ────────────────────────────────────────────────

    [Fact]
    public async Task The_summary_names_the_blockers_and_the_status_is_the_worst_row()
    {
        File.Delete(_harness.PlanPath);
        File.WriteAllText(_harness.ClaudeConfigPath, "{}");

        var result = await _harness.CheckAsync();

        Assert.Equal(PreflightStatus.Error, result.Status);
        Assert.False(result.CanRun);
        Assert.StartsWith("Blocked:", result.Summary);
        Assert.Contains(NameOfPlanFile, result.Summary);
        Assert.Single(result.Errors, check => check.Id == PreflightCheckId.PlanFile);
        Assert.Contains(result.Warnings, check => check.Id == PreflightCheckId.WorkspaceTrust);
    }

    [Fact]
    public async Task Warnings_alone_leave_the_run_possible()
    {
        File.WriteAllText(_harness.ClaudeConfigPath, "{}");
        _harness.AutoModeUnavailable();

        var result = await _harness.CheckAsync();

        Assert.Equal(PreflightStatus.Warning, result.Status);
        Assert.True(result.CanRun);
        Assert.StartsWith("Ready, with warnings:", result.Summary);
    }

    static string NameOfPlanFile => PreflightChecker.NameOf(PreflightCheckId.PlanFile);

    // ── Fix actions are data, and Core applies only the ones it owns ───────────────────────────

    [Theory]
    [InlineData(PreflightFixCommand.SetClaudeExecutablePath)]
    [InlineData(PreflightFixCommand.SetProjectDirectory)]
    [InlineData(PreflightFixCommand.OpenPlanFile)]
    [InlineData(PreflightFixCommand.ShowLoginInstructions)]
    [InlineData(PreflightFixCommand.OpenSettingsFile)]
    public async Task The_ui_owned_fixes_are_reported_as_the_apps_to_perform(PreflightFixCommand command)
    {
        var fix = new PreflightFixAction(command, "label");
        Assert.False(fix.IsAppliedByCore);

        var applied = await _harness.Create().ApplyFixAsync(fix, _harness.Settings);

        Assert.Equal(PreflightFixOutcome.HandledByTheApp, applied.Outcome);
        Assert.False(applied.Succeeded);
    }

    [Fact]
    public async Task Creating_a_starter_plan_file_writes_one_with_unticked_boxes()
    {
        File.Delete(_harness.PlanPath);
        var fix = (await _harness.CheckAsync(PreflightCheckId.PlanFile)).Fix!;

        var applied = await _harness.Create().ApplyFixAsync(fix, _harness.Settings);

        Assert.Equal(PreflightFixOutcome.Applied, applied.Outcome);
        Assert.True(PreflightChecker.CountPlanItems(File.ReadAllText(_harness.PlanPath)).HasRemainingWork);
        Assert.Equal(PreflightStatus.Ok, (await _harness.CheckAsync(PreflightCheckId.PlanFile)).Status);
    }

    [Fact]
    public async Task Creating_a_plan_file_that_already_exists_changes_nothing()
    {
        var before = File.ReadAllText(_harness.PlanPath);
        var fix = new PreflightFixAction(PreflightFixCommand.CreatePlanFile, "create", _harness.PlanPath);

        var applied = await _harness.Create().ApplyFixAsync(fix, _harness.Settings);

        Assert.Equal(PreflightFixOutcome.AlreadyDone, applied.Outcome);
        Assert.Equal(before, File.ReadAllText(_harness.PlanPath));
    }

    [Fact]
    public async Task Trusting_the_folder_writes_the_keys_and_turns_the_row_green()
    {
        File.WriteAllText(_harness.ClaudeConfigPath, """{ "someOtherKey": 1 }""");
        var fix = (await _harness.CheckAsync(PreflightCheckId.WorkspaceTrust)).Fix!;

        var applied = await _harness.Create().ApplyFixAsync(fix, _harness.Settings);

        Assert.Equal(PreflightFixOutcome.Applied, applied.Outcome);
        Assert.Contains("someOtherKey", File.ReadAllText(_harness.ClaudeConfigPath));
        Assert.Equal(PreflightStatus.Ok, (await _harness.CheckAsync(PreflightCheckId.WorkspaceTrust)).Status);
    }

    [Fact]
    public async Task Installing_caveman_runs_the_two_plan_md_commands_in_order()
    {
        var applied = await _harness.Create().ApplyFixAsync(
            new PreflightFixAction(PreflightFixCommand.InstallCavemanPlugin, "install"),
            _harness.Settings);

        Assert.Equal(PreflightFixOutcome.Applied, applied.Outcome);
        Assert.Equal(
            ["plugin marketplace add JuliusBrussee/caveman", "plugin install caveman@caveman"],
            _harness.Runner.Calls.Select(call => call.Arguments).ToArray());
        Assert.All(_harness.Runner.Calls, call => Assert.Equal(_harness.ClaudeExecutable, call.Executable));
    }

    [Fact]
    public async Task A_failed_install_reports_the_failure_and_stops_at_the_first_command()
    {
        _harness.Runner.Responses["plugin marketplace add JuliusBrussee/caveman"] =
            new PreflightProcessResult(1, string.Empty, "network unreachable");

        var applied = await _harness.Create().ApplyFixAsync(
            new PreflightFixAction(PreflightFixCommand.InstallCavemanPlugin, "install"),
            _harness.Settings);

        Assert.Equal(PreflightFixOutcome.Failed, applied.Outcome);
        Assert.Contains("network unreachable", applied.Message);
        Assert.Single(_harness.Runner.Calls);
    }

    [Fact]
    public async Task Git_init_runs_in_the_project_directory()
    {
        var applied = await _harness.Create().ApplyFixAsync(
            new PreflightFixAction(
                PreflightFixCommand.InitializeGitRepository, "init", _harness.ProjectDirectory),
            _harness.Settings);

        Assert.Equal(PreflightFixOutcome.Applied, applied.Outcome);
        var call = Assert.Single(_harness.Runner.Calls);
        Assert.Equal(PreflightChecker.GitExecutable, call.Executable);
        Assert.Equal("init", call.Arguments);
        Assert.Equal(_harness.ProjectDirectory, call.WorkingDirectory);
    }

    [Fact]
    public async Task Re_probing_auto_mode_reports_the_new_answer()
    {
        _harness.AutoModeUnavailable();
        var checker = _harness.Create();
        Assert.Equal(PreflightStatus.Warning, (await checker.CheckAsync(_harness.Settings))
            .Find(PreflightCheckId.AutoMode)!.Status);

        _harness.AutoModeAvailable();
        var applied = await checker.ApplyFixAsync(
            new PreflightFixAction(PreflightFixCommand.ReprobeAutoMode, "re-probe"), _harness.Settings);

        Assert.Equal(PreflightFixOutcome.Applied, applied.Outcome);
    }

    [Fact]
    public async Task A_fix_that_fails_comes_back_as_a_failure_not_an_exception()
    {
        var fix = new PreflightFixAction(
            PreflightFixCommand.InitializeGitRepository, "init", Path.Combine(_harness.Root, "nowhere"));

        var applied = await _harness.Create().ApplyFixAsync(fix, _harness.Settings);

        Assert.Equal(PreflightFixOutcome.Failed, applied.Outcome);
        Assert.False(applied.Succeeded);
    }

    // ── Harness ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A machine where everything is fine, with one knob per failure mode. Every path is inside a
    /// temp directory and every process is faked.
    /// </summary>
    sealed class Harness : IDisposable
    {
        readonly TempDirectory _temp = new();

        public Harness()
        {
            Directory.CreateDirectory(ProjectDirectory);
            File.WriteAllText(PlanPath, "- [x] Done already\n- [ ] The next thing\n");

            Directory.CreateDirectory(Path.Combine(CavemanInstallPath, "commands"));
            File.WriteAllText(Path.Combine(CavemanInstallPath, "commands", "caveman.md"), "# caveman");
            WritePluginManifest($$"""
                {
                  "version": 2,
                  "plugins": {
                    "caveman@caveman": [
                      { "scope": "user", "installPath": {{JsonSerializer.Serialize(CavemanInstallPath)}} }
                    ]
                  }
                }
                """);

            WriteTrustedConfig();

            Locator
                .Locate(Arg.Any<PilotSettings>())
                .Returns(new ClaudeExecutableResolution(
                    ClaudeExecutable, ClaudeExecutableSource.SearchPath, null, [ClaudeExecutable]));

            Credentials
                .ReadAsync(Arg.Any<CancellationToken>())
                .Returns(new ClaudeCredentials("token", Clock.GetUtcNow().AddHours(4)));

            AutoModeAvailable();

            Runner.Responses["--version"] = new PreflightProcessResult(0, "2.1.220 (Claude Code)", "");
            Runner.Responses["rev-parse --is-inside-work-tree"] = new PreflightProcessResult(0, "true\n", "");
            Runner.Responses["status --porcelain"] = new PreflightProcessResult(0, "", "");
            Runner.Responses["init"] = new PreflightProcessResult(0, "", "");
            Runner.Responses["plugin marketplace add JuliusBrussee/caveman"] =
                new PreflightProcessResult(0, "", "");
            Runner.Responses["plugin install caveman@caveman"] = new PreflightProcessResult(0, "", "");

            Settings = new PilotSettings { ProjectDirectory = ProjectDirectory };
        }

        public FakeProcessRunner Runner { get; } = new();

        public IClaudeExecutableLocator Locator { get; } = Substitute.For<IClaudeExecutableLocator>();

        public IClaudeCredentialReader Credentials { get; } = Substitute.For<IClaudeCredentialReader>();

        public IAutoModeProbeRunner AutoModeRunner { get; } = Substitute.For<IAutoModeProbeRunner>();

        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.Parse("2026-07-26T12:00:00Z"));

        public PilotSettings Settings { get; set; }

        public string Root => _temp.Path;

        public string ProjectDirectory => Path.Combine(_temp.Path, "project");

        public string PlanPath => Path.Combine(ProjectDirectory, "plan.md");

        public string ClaudeExecutable => Path.Combine(_temp.Path, "bin", "claude.cmd");

        public string PluginsDirectory => Path.Combine(_temp.Path, "home", ".claude", "plugins");

        public string PluginManifestPath =>
            Path.Combine(PluginsDirectory, PreflightChecker.InstalledPluginsFileName);

        public string CavemanInstallPath =>
            Path.Combine(PluginsDirectory, "cache", "caveman", "caveman", "0d95a81d35a9");

        public string ClaudeConfigPath => Path.Combine(_temp.Path, "home", ".claude.json");

        public string UserSettingsPath =>
            Path.Combine(_temp.Path, "home", ".claude", "settings.json");

        public void Dispose() => _temp.Dispose();

        public PreflightChecker Create() => new(
            Locator,
            Runner,
            new AutoModeProbe(AutoModeRunner, NullLogger<AutoModeProbe>.Instance, ClaudeExecutable),
            new WorkspaceTrustManager(NullLogger<WorkspaceTrustManager>.Instance, ClaudeConfigPath),
            new PermissionsAskScanner(NullLogger<PermissionsAskScanner>.Instance, UserSettingsPath),
            Credentials,
            NullLogger<PreflightChecker>.Instance,
            Clock,
            PluginsDirectory);

        public Task<PreflightResult> CheckAsync() => Create().CheckAsync(Settings);

        public async Task<PreflightCheck> CheckAsync(PreflightCheckId id) =>
            (await CheckAsync()).Find(id)!;

        public void AutoModeAvailable() => AutoModeRunner
            .RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new AutoModeProbeOutput(0, """{ "enabled": true }""", ""));

        public void AutoModeUnavailable() => AutoModeRunner
            .RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new AutoModeProbeOutput(1, "", "unknown command: auto-mode"));

        public void WritePluginManifest(string json)
        {
            Directory.CreateDirectory(PluginsDirectory);
            File.WriteAllText(PluginManifestPath, json);
        }

        public void WriteUserSettings(string json)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UserSettingsPath)!);
            File.WriteAllText(UserSettingsPath, json);
        }

        /// <summary>Writes a <c>.claude.json</c> in which the project folder is already trusted.</summary>
        void WriteTrustedConfig()
        {
            var projects = string.Join(",\n", WorkspaceTrustManager
                .TrustKeysFor(ProjectDirectory)
                .Select(key =>
                    $"    {JsonSerializer.Serialize(key)}: {{ " +
                    string.Join(", ", WorkspaceTrustManager.TrustFlags.Select(flag => $"\"{flag}\": true")) +
                    " }"));

            Directory.CreateDirectory(Path.GetDirectoryName(ClaudeConfigPath)!);
            File.WriteAllText(ClaudeConfigPath, $"{{\n  \"projects\": {{\n{projects}\n  }}\n}}\n");
        }
    }

    /// <summary>
    /// Scripted stand-in for <see cref="IPreflightProcessRunner"/>, keyed by the joined argument list.
    /// Its existence is the guarantee that no test here can start a real <c>claude</c>.
    /// </summary>
    sealed class FakeProcessRunner : IPreflightProcessRunner
    {
        public List<(string Executable, string Arguments, string? WorkingDirectory)> Calls { get; } = [];

        public Dictionary<string, PreflightProcessResult> Responses { get; } = new(StringComparer.Ordinal);

        /// <summary>Returned for an argument list nothing was scripted for: "could not start".</summary>
        public PreflightProcessResult Default { get; set; } =
            new(-1, string.Empty, string.Empty, Started: false);

        /// <summary>When set, every call throws it — the "a dependency exploded" case.</summary>
        public Exception? Throw { get; set; }

        public Task<PreflightProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string? workingDirectory,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var joined = string.Join(' ', arguments);
            Calls.Add((executable, joined, workingDirectory));

            if (Throw is { } exception)
            {
                throw exception;
            }

            return Task.FromResult(Responses.TryGetValue(joined, out var response) ? response : Default);
        }
    }
    // ── Remote control (plan.md §5.5) ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Remote_control_is_not_checked_when_it_is_off()
    {
        var result = await _harness.CheckAsync();

        Assert.Null(result.Find(PreflightCheckId.RemoteControl));
    }

    [Fact]
    public async Task Remote_control_warns_when_the_launch_mode_cannot_deliver_it()
    {
        // Claude Code accepts --remote-control in headless mode and ignores it, so a ticked box
        // would otherwise imply something that is not happening.
        _harness.Settings = _harness.Settings with
        {
            EnableRemoteControl = true,
            LaunchMode = LaunchMode.Headless,
        };

        var result = await _harness.CheckAsync();
        var check = result.Find(PreflightCheckId.RemoteControl)!;

        Assert.Equal(PreflightStatus.Warning, check.Status);
        Assert.Contains("Visible terminal", check.Message, StringComparison.Ordinal);

        // A warning must never block a run: the run itself is fine.
        Assert.True(result.CanRun);
    }

    [Fact]
    public async Task Remote_control_reports_the_session_name_in_terminal_mode()
    {
        _harness.Settings = _harness.Settings with
        {
            EnableRemoteControl = true,
            LaunchMode = LaunchMode.VisibleTerminal,
            RemoteControlName = "chosen-name",
        };

        var check = await _harness.CheckAsync(PreflightCheckId.RemoteControl);

        Assert.Equal(PreflightStatus.Ok, check.Status);
        Assert.Contains("chosen-name", check.Message, StringComparison.Ordinal);
    }

}
