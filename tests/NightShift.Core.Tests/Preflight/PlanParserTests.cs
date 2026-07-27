using NightShift.Core.Configuration;
using NightShift.Core.Preflight;

namespace NightShift.Core.Tests.Preflight;

public sealed class PlanParserTests
{
    // A trimmed-down shape of a real milestone plan: two milestones carry explicit markers, the ones
    // before them carry none, and there are ordinary headings that must not be mistaken for
    // milestones.
    const string MilestonePlan = """
        # Something — Master Plan

        ## 11. Milestones

        ### M0 — Scaffold, CI (S)
        **Goal:** a skeleton.

        ### M1 — Layout engine spike (L)
        **Goal:** prove the hard thing.

        ### M2 — Document model (M)
        **Goal:** round-trip.

        ### M3 — Packaging (S/M) — **delivered 2026-07-26**
        **Status:** implemented; design in `docs/M3-spec.md`.

        ### M4 — Action panel (M) — **delivered 2026-07-27**
        **Status:** implemented.
        #### The sibling trap — one new command
        Deeper headings belong to the milestone above them.

        ### M5 — Address book (L)
        **Goal:** import the member list once.

        ### Sizing & sequencing notes
        - M1 before everything.

        ## 12. Verification
        """;

    [Fact]
    public void A_milestone_plan_is_detected_from_its_headings() =>
        Assert.Equal(PlanFormat.Milestone, PlanParser.Detect(MilestonePlan));

    [Fact]
    public void Checkboxes_win_when_a_plan_has_both_and_one_is_still_unticked()
    {
        const string Both = """
            ## M1 — a milestone heading

            ### Phase 0
            - [x] done
            - [ ] not done
            """;

        Assert.Equal(PlanFormat.Checkbox, PlanParser.Detect(Both));
    }

    // The shape that made a finished 16-milestone plan read as "4 of 13 items complete": every
    // milestone delivered, and a tail list of open items nobody intends as the unit of progress.
    // Ticked and blocked checkboxes are a record of work, not work — so they do not settle the format.
    [Fact]
    public void Milestones_win_when_the_only_checkboxes_left_are_ticked_or_blocked()
    {
        const string DeliveredWithLeftovers = """
            ## 11. Milestones

            ### M0 — Scaffold (S) — **delivered 2026-07-26**
            **Status:** implemented.

            ### M1 — Everything else (L) — **delivered 2026-07-27**
            **Status:** implemented.

            ## 12. What is left

            - [x] a cosmetic defect, since fixed
            - [!] needs a person with a screen reader
            """;

        var result = PlanParser.Parse(DeliveredWithLeftovers);

        Assert.Equal(PlanFormat.Milestone, result.Format);
        Assert.Equal(new PlanItemCounts(2, 0, 0, PlanFormat.Milestone), result.Counts);
    }

    // A checkbox plan that happens to be finished stays a checkbox plan: with no milestone headings
    // there is nothing to switch to, and the "every checkbox is already ticked" warning is the useful
    // one.
    [Fact]
    public void A_fully_ticked_checkbox_plan_with_no_milestones_stays_a_checkbox_plan()
    {
        var result = PlanParser.Parse("## Phase 0\n\n- [x] done\n- [!] blocked\n");

        Assert.Equal(PlanFormat.Checkbox, result.Format);
        Assert.Equal(new PlanItemCounts(1, 0, 1), result.Counts);
    }

    [Fact]
    public void A_plan_with_neither_reads_as_an_empty_checkbox_plan()
    {
        var result = PlanParser.Parse("# Notes\n\nJust prose.\n");

        Assert.Equal(PlanFormat.Checkbox, result.Format);
        Assert.Equal(0, result.Counts.Total);
    }

    [Fact]
    public void Delivered_milestones_backfill_every_lower_numbered_one()
    {
        var counts = PlanParser.CountMilestones(MilestonePlan);

        // M0–M2 carry no marker at all but sit below the delivered M4, so they count as done. M5 is
        // the only one left. Without the backfill this file would read "2 of 6".
        Assert.Equal(6, counts.Total);
        Assert.Equal(5, counts.Completed);
        Assert.Equal(1, counts.Remaining);
        Assert.Equal(0, counts.Blocked);
        Assert.Equal("5 of 6 milestones complete", counts.Summary);
    }

    [Fact]
    public void An_explicit_block_survives_the_backfill()
    {
        const string Plan = """
            ### M0 — first
            **Blocked:** needs a decision from a human.

            ### M1 — second

            ### M2 — third — **delivered 2026-07-27**
            """;

        var counts = PlanParser.CountMilestones(Plan);

        // M0 is below the delivered M2 and would otherwise be backfilled to complete. A milestone
        // someone deliberately flagged must never be quietly counted as done.
        Assert.Equal(1, counts.Blocked);
        Assert.Equal(2, counts.Completed);
        Assert.Equal(0, counts.Remaining);
    }

    [Fact]
    public void A_status_line_alone_marks_a_milestone_delivered()
    {
        const string Plan = """
            ### M0 — first
            **Status:** implemented.

            ### M1 — second
            **Goal:** not yet.
            """;

        var counts = PlanParser.CountMilestones(Plan);

        Assert.Equal(1, counts.Completed);
        Assert.Equal(1, counts.Remaining);
    }

    [Fact]
    public void A_post_milestone_status_line_also_counts()
    {
        const string Plan = """
            ### M0 — first
            **Post-milestone status (2026-07-26):** done.

            ### M1 — second
            """;

        Assert.Equal(1, PlanParser.CountMilestones(Plan).Completed);
    }

    [Theory]
    [InlineData("## M1 — two hashes")]
    [InlineData("### M1 — three hashes")]
    [InlineData("#### M1 — four hashes")]
    public void Milestone_headings_are_recognised_at_every_level(string heading) =>
        Assert.Equal(1, PlanParser.CountMilestones(heading).Total);

    [Theory]
    [InlineData("### Sizing & sequencing notes")]
    [InlineData("#### The catalog — 20 families / 58 faces")]
    [InlineData("## Mandatory plugin integration")]
    [InlineData("### Migrations and other Mnemonics")]
    [InlineData("# M1 — a single hash is a document title, not a milestone")]
    public void Ordinary_headings_are_not_milestones(string heading) =>
        Assert.Equal(0, PlanParser.CountMilestones(heading).Total);

    [Fact]
    public void Milestones_inside_a_fenced_block_are_ignored()
    {
        // A plan that documents its own conventions shows an example milestone in a fence. Counting
        // it would make the project card quietly wrong — the same rule the checkbox counter follows.
        const string Plan = """
            # How to write this plan

            ```
            ### M1 — example heading — **delivered 2026-01-01**
            ```

            ### M7 — the only real one
            """;

        var counts = PlanParser.CountMilestones(Plan);

        Assert.Equal(1, counts.Total);
        Assert.Equal(1, counts.Remaining);
    }

    [Fact]
    public void A_deeper_subheading_does_not_end_its_milestone()
    {
        const string Plan = """
            ### M3 — a milestone
            #### A subsection of it
            **Status:** implemented.
            """;

        Assert.Equal(1, PlanParser.CountMilestones(Plan).Completed);
    }

    [Fact]
    public void A_shallower_heading_ends_its_milestone()
    {
        const string Plan = """
            ### M3 — a milestone

            ## A new top-level section
            **Status:** this belongs to the section, not to M3.
            """;

        Assert.Equal(1, PlanParser.CountMilestones(Plan).Remaining);
    }

    [Fact]
    public void An_explicit_format_is_obeyed_even_when_it_looks_wrong()
    {
        // A user who picked a format has a reason; silently overriding them would be worse than an
        // empty tally, and the preflight warning says plainly that nothing was found.
        var result = PlanParser.Parse("- [ ] a checkbox", PlanFormat.Milestone);

        Assert.Equal(PlanFormat.Milestone, result.Format);
        Assert.Equal(0, result.Counts.Total);
    }

    [Fact]
    public void Checkbox_counts_keep_their_wording()
    {
        var counts = PlanParser.CountCheckboxes("- [x] a\n- [ ] b\n- [!] c\n");

        Assert.Equal("1 of 3 items complete", counts.Summary);
        Assert.Equal("item", counts.Noun);
    }

    [Fact]
    public void Empty_input_parses_as_an_empty_checkbox_plan()
    {
        var result = PlanParser.Parse(null);

        Assert.Equal(PlanFormat.Checkbox, result.Format);
        Assert.Same(PlanItemCounts.Empty, PlanParser.CountCheckboxes(null));
        Assert.Equal(0, result.Counts.Total);
    }
}
