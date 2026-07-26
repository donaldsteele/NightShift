using NightShift.Core.Configuration;
using NightShift.Core.Execution;

namespace NightShift.Core.Tests.Execution;

public sealed class ClaudeArgumentsBuilderTests
{
    static IReadOnlyList<string> Build(PilotSettings? settings = null, string? resume = null) =>
        ClaudeArgumentsBuilder.Build(
            settings ?? new PilotSettings(),
            "do the thing",
            PermissionMode.Auto,
            PilotSettings.DefaultAllowedTools,
            resume);

    [Fact]
    public void The_argument_list_never_contains_bare()
    {
        // plan.md §11.3: --bare skips skills, plugins, hooks, MCP and OAuth auth. It would break
        // both /caveman and subscription authentication.
        Assert.DoesNotContain("--bare", Build());
    }

    [Fact]
    public void The_argument_list_never_selects_plan_mode()
    {
        // plan.md §5.3.3: plan mode ends by waiting for a human to approve.
        var arguments = Build();

        Assert.DoesNotContain("plan", arguments);
        Assert.Contains("--permission-mode", arguments);
    }

    [Theory]
    [InlineData(PermissionMode.Auto, "auto")]
    [InlineData(PermissionMode.AcceptEdits, "acceptEdits")]
    [InlineData(PermissionMode.BypassPermissions, "bypassPermissions")]
    public void The_permission_mode_flag_uses_the_exact_cli_spelling(PermissionMode mode, string expected)
    {
        var arguments = ClaudeArgumentsBuilder.Build(new PilotSettings(), "p", mode, "Read");

        var index = arguments.ToList().IndexOf("--permission-mode");
        Assert.True(index >= 0);
        Assert.Equal(expected, arguments[index + 1]);
    }

    [Fact]
    public void AskUserQuestion_is_always_disallowed()
    {
        var arguments = Build();

        var index = arguments.ToList().IndexOf("--disallowedTools");
        Assert.True(index >= 0);
        Assert.Equal("AskUserQuestion", arguments[index + 1]);
    }

    [Fact]
    public void The_stream_json_flags_are_all_present()
    {
        var arguments = Build();

        Assert.Contains("-p", arguments);
        Assert.Contains("--output-format", arguments);
        Assert.Contains("stream-json", arguments);
        Assert.Contains("--verbose", arguments);
        Assert.Contains("--include-partial-messages", arguments);
    }

    [Fact]
    public void The_prompt_is_passed_as_one_argument_however_awkward_it_is()
    {
        const string prompt = "line one\nline two with \"quotes\" and 100% and a trailing backslash\\";

        var arguments = ClaudeArgumentsBuilder.Build(new PilotSettings(), prompt, PermissionMode.Auto, "Read");

        Assert.Contains(prompt, arguments);
    }

    [Fact]
    public void Model_is_omitted_when_blank_and_passed_when_set()
    {
        Assert.DoesNotContain("--model", Build());

        var withModel = Build(new PilotSettings { Model = " claude-opus-5 " });
        var index = withModel.ToList().IndexOf("--model");
        Assert.True(index >= 0);
        Assert.Equal("claude-opus-5", withModel[index + 1]);
    }

    [Fact]
    public void Resume_is_only_passed_when_the_strategy_asks_for_it()
    {
        Assert.DoesNotContain("--resume", Build(new PilotSettings { ResumeStrategy = ResumeStrategy.Fresh }, "sess-1"));
        Assert.DoesNotContain("--resume", Build(new PilotSettings { ResumeStrategy = ResumeStrategy.Resume }, null));

        var resumed = Build(new PilotSettings { ResumeStrategy = ResumeStrategy.Resume }, "sess-1");
        var index = resumed.ToList().IndexOf("--resume");
        Assert.True(index >= 0);
        Assert.Equal("sess-1", resumed[index + 1]);
    }

    [Fact]
    public void Skip_permissions_is_only_emitted_alongside_bypass_permissions()
    {
        var optedIn = new PilotSettings { SkipPermissionsFallback = true };

        Assert.DoesNotContain(
            "--dangerously-skip-permissions",
            ClaudeArgumentsBuilder.Build(optedIn, "p", PermissionMode.Auto, "Read"));

        Assert.Contains(
            "--dangerously-skip-permissions",
            ClaudeArgumentsBuilder.Build(optedIn, "p", PermissionMode.BypassPermissions, "Read"));
    }

    [Theory]
    [InlineData(PermissionMode.Auto)]
    [InlineData(PermissionMode.AcceptEdits)]
    public void Skip_permissions_is_refused_for_every_other_mode(PermissionMode mode) =>
        Assert.DoesNotContain(
            "--dangerously-skip-permissions",
            ClaudeArgumentsBuilder.Build(
                new PilotSettings { SkipPermissionsFallback = true }, "p", mode, "Read"));

    [Fact]
    public void A_blank_allowed_tools_list_omits_the_flag() =>
        Assert.DoesNotContain("--allowedTools", ClaudeArgumentsBuilder.Build(new PilotSettings(), "p", PermissionMode.Auto, "  "));
}
