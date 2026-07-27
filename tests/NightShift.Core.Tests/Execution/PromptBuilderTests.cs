using NightShift.Core.Configuration;
using NightShift.Core.Execution;

namespace NightShift.Core.Tests.Execution;

public sealed class PromptBuilderTests
{
    readonly PromptBuilder _builder = new();

    [Fact]
    public void The_default_settings_produce_the_prompt_from_the_plan()
    {
        // plan.md §5.2, verbatim. Line endings are normalised on both sides because the source of
        // the default template is a raw string literal and carries the checkout's own endings.
        const string Expected = """
            /caveman:caveman full

            Continue work on this project.

            Read `plan.md` in this directory. It is the authoritative task list.

            Pick up from the first unchecked item in plan.md and make concrete progress.
            - After finishing an item, tick its checkbox.
            - If an item is ambiguous or blocked, mark it `- [!]` with a one-line note
              explaining the blocker, then move to the next item.
            - Prefer finishing one item completely over starting several.

            Rules for this session:
            - You are running unattended. There is no human available to answer questions.
              Never ask for confirmation, clarification, or approval — decide and proceed.
            - Work only on what plan.md already describes. Do not invent new scope.
            - Commit your work, using a conventional commit message.
            - Run the project's tests before you finish. If they fail, fix them.
            - End your run in a clean state: no half-applied edits, everything committed.
            """;

        Assert.Equal(Expected.ReplaceLineEndings("\n"), _builder.Build(new PilotSettings()));
    }

    [Fact]
    public void A_milestone_plan_is_told_to_mark_milestones_delivered()
    {
        // The exact markers matter: PlanParser reads these back, so a run that follows this prompt is
        // what makes the dashboard's tally move.
        const string Expected = """
            /caveman:caveman full

            Continue work on this project.

            Read `plan.md` in this directory. It is the authoritative task list.

            Work the lowest-numbered milestone in plan.md that is not yet marked delivered,
            and complete the whole of it.
            - When it is done, append ` — **delivered YYYY-MM-DD**` to that milestone's heading
              and add a `**Status:**` line under it saying what shipped.
            - If the milestone is ambiguous or blocked, add a `**Blocked:**` line under its
              heading with a one-line reason, then move to the next milestone.
            - Prefer finishing one milestone completely over starting several.

            Rules for this session:
            - You are running unattended. There is no human available to answer questions.
              Never ask for confirmation, clarification, or approval — decide and proceed.
            - Work only on what plan.md already describes. Do not invent new scope.
            - Commit your work, using a conventional commit message.
            - Run the project's tests before you finish. If they fail, fix them.
            - End your run in a clean state: no half-applied edits, everything committed.
            """;

        Assert.Equal(
            Expected.ReplaceLineEndings("\n"),
            _builder.Build(new PilotSettings { PlanFormat = PlanFormat.Milestone }));
    }

    [Fact]
    public void An_unresolved_format_falls_back_to_the_checkbox_conventions() =>
        Assert.Equal(
            _builder.Build(new PilotSettings { PlanFormat = PlanFormat.Checkbox }),
            _builder.Build(new PilotSettings { PlanFormat = PlanFormat.Auto }));

    [Fact]
    public void A_template_without_the_conventions_token_is_left_alone()
    {
        const string Template = "Do the work described in {planFile}.";

        Assert.Equal(
            "Do the work described in plan.md.",
            PromptBuilder.ApplyPlanFile(Template, "plan.md", PlanFormat.Milestone));
    }

    [Fact]
    public void The_caveman_directive_is_the_first_line_and_is_followed_by_a_blank_one()
    {
        var lines = _builder.Build(new PilotSettings()).Split('\n');

        Assert.Equal("/caveman:caveman full", lines[0]);
        Assert.Equal(string.Empty, lines[1]);
        Assert.Equal("Continue work on this project.", lines[2]);
    }

    [Theory]
    [InlineData(CavemanLevel.Lite, "/caveman:caveman lite")]
    [InlineData(CavemanLevel.Full, "/caveman:caveman full")]
    [InlineData(CavemanLevel.Ultra, "/caveman:caveman ultra")]
    [InlineData(CavemanLevel.WenyanLite, "/caveman:caveman wenyan-lite")]
    [InlineData(CavemanLevel.WenyanFull, "/caveman:caveman wenyan-full")]
    [InlineData(CavemanLevel.WenyanUltra, "/caveman:caveman wenyan-ultra")]
    public void Every_caveman_level_reaches_the_prompt(CavemanLevel level, string expected)
    {
        Assert.Equal(expected, PromptBuilder.BuildCavemanDirective(level));
        Assert.StartsWith(expected + "\n\n", _builder.Build(new PilotSettings { CavemanLevel = level }), StringComparison.Ordinal);
    }

    [Fact]
    public void The_level_lives_only_in_the_prepended_line_never_in_the_template()
    {
        // The template is the user's to edit; if it carried the directive, changing the dropdown
        // would have to rewrite their prose.
        Assert.DoesNotContain(PromptBuilder.CavemanCommand, PilotSettings.DefaultPromptTemplate, StringComparison.Ordinal);

        var prompt = _builder.Build(new PilotSettings { CavemanLevel = CavemanLevel.Ultra });

        Assert.Equal(1, prompt.Split(PromptBuilder.CavemanCommand).Length - 1);
    }

    [Fact]
    public void Every_occurrence_of_the_plan_file_token_is_substituted()
    {
        var settings = new PilotSettings { PlanFileName = "roadmap.md" };

        var prompt = _builder.Build(settings);

        Assert.DoesNotContain(PromptBuilder.PlanFileToken, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("plan.md", prompt, StringComparison.Ordinal);

        // The rendered prompt names the plan file three times — twice from the template and once from
        // the conventions block it pulls in. A single-replacement bug would leave the run reading a
        // file that does not exist.
        Assert.Equal(3, prompt.Split("roadmap.md").Length - 1);
    }

    [Fact]
    public void Several_tokens_on_one_line_are_all_substituted() =>
        Assert.Equal(
            "TASKS.md and TASKS.md and TASKS.md",
            PromptBuilder.ApplyPlanFile("{planFile} and {planFile} and {planFile}", "TASKS.md"));

    [Fact]
    public void A_template_with_no_token_is_left_alone() =>
        Assert.Equal(
            "Just keep going.",
            PromptBuilder.ApplyPlanFile("Just keep going.", "roadmap.md"));

    [Fact]
    public void The_token_is_matched_exactly()
    {
        // Only the documented spelling substitutes; a near-miss stays visible in the preview rather
        // than silently doing nothing useful.
        Assert.Equal("{PlanFile} {planfile}", PromptBuilder.ApplyPlanFile("{PlanFile} {planfile}", "roadmap.md"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_template_falls_back_to_the_default(string? template)
    {
        var prompt = PromptBuilder.Build(CavemanLevel.Full, template, "plan.md");

        Assert.Equal(_builder.Build(new PilotSettings()), prompt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_plan_file_name_falls_back_to_plan_md(string? planFileName) =>
        Assert.Equal("plan.md", PromptBuilder.ApplyPlanFile("{planFile}", planFileName));

    [Fact]
    public void A_plan_file_name_is_trimmed() =>
        Assert.Equal("roadmap.md", PromptBuilder.ApplyPlanFile("{planFile}", "  roadmap.md  "));

    [Fact]
    public void Line_endings_are_normalised_so_the_prompt_is_byte_identical_everywhere()
    {
        var crlf = PromptBuilder.Build(CavemanLevel.Full, "one\r\ntwo\rthree\nfour", "plan.md");

        Assert.Equal("/caveman:caveman full\n\none\ntwo\nthree\nfour", crlf);
        Assert.DoesNotContain('\r', crlf);
    }

    [Fact]
    public void Surrounding_whitespace_in_the_template_never_reaches_the_prompt() =>
        Assert.Equal(
            "/caveman:caveman full\n\nGo.",
            PromptBuilder.Build(CavemanLevel.Full, "\n\n  Go.\n\n  ", "plan.md"));

    [Fact]
    public void The_same_settings_always_produce_the_same_string()
    {
        // The Settings screen renders this on every keystroke as a live preview (plan.md §9.2), so
        // it has to be pure — no clock, no environment, no run counter.
        var settings = new PilotSettings { PlanFileName = "roadmap.md", CavemanLevel = CavemanLevel.Ultra };

        Assert.Equal(_builder.Build(settings), new PromptBuilder().Build(settings));
        Assert.Equal(
            _builder.Build(settings),
            PromptBuilder.Build(settings.CavemanLevel, settings.PromptTemplate, settings.PlanFileName));
    }

    [Fact]
    public void The_prompt_tells_Claude_it_is_unattended()
    {
        // plan.md §5.3.3 requires this to be in the template; a run that stops to ask a question is
        // the failure mode the whole app exists to avoid.
        var prompt = _builder.Build(new PilotSettings());

        Assert.Contains("running unattended", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Never ask for confirmation", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void A_settings_record_is_required() =>
        Assert.Throws<ArgumentNullException>(() => _builder.Build(null!));

    [Fact]
    public void The_builder_is_reachable_through_its_interface()
    {
        IPromptBuilder builder = _builder;

        Assert.StartsWith("/caveman:caveman full", builder.Build(new PilotSettings()), StringComparison.Ordinal);
    }
}
