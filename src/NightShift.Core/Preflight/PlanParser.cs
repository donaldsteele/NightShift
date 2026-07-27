using System.Text.RegularExpressions;
using NightShift.Core.Configuration;

namespace NightShift.Core.Preflight;

/// <summary>
/// Reads a plan file's progress, in either of the two conventions <see cref="PlanFormat"/> describes
/// (plan.md §9.1).
/// </summary>
/// <remarks>
/// <para>
/// Both parsers share one line walk, and in particular one rule about fenced code blocks: a plan that
/// documents its own conventions — as this repo's does — shows example checkboxes and example
/// milestone headings inside a fence, and counting those would make the dashboard's "12 of 30"
/// quietly wrong.
/// </para>
/// <para>
/// Nothing here decides anything. It produces a tally for the project card and a warning row, and
/// the resolved format that picks the prompt's conventions. The run itself is still driven by the
/// prompt: Claude reads the plan and chooses the next piece of work.
/// </para>
/// </remarks>
public static partial class PlanParser
{
    /// <summary>What one plan file turned out to be, and how much of it is done.</summary>
    /// <param name="Format">
    /// Always <see cref="PlanFormat.Checkbox"/> or <see cref="PlanFormat.Milestone"/> — never
    /// <see cref="PlanFormat.Auto"/>, which is a request, not an answer.
    /// </param>
    public sealed record PlanParseResult(PlanFormat Format, PlanItemCounts Counts);

    /// <summary>
    /// Parses <paramref name="planText"/> in the requested format, detecting it when asked to.
    /// </summary>
    /// <param name="planText">The whole plan file. Null or empty parses as an empty checkbox plan.</param>
    /// <param name="requested">
    /// The user's <see cref="PlanFormat"/> setting. <see cref="PlanFormat.Auto"/> detects; anything
    /// else is obeyed even when the file looks like the other kind, because a user who has picked a
    /// format has a reason and silently overriding them would be worse than an empty tally.
    /// </param>
    public static PlanParseResult Parse(string? planText, PlanFormat requested = PlanFormat.Auto)
    {
        var format = requested == PlanFormat.Auto ? Detect(planText) : requested;

        var counts = format == PlanFormat.Milestone
            ? CountMilestones(planText)
            : CountCheckboxes(planText);

        return new PlanParseResult(format, counts);
    }

    /// <summary>
    /// Which convention a plan file uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A file with no <c>M&lt;n&gt;</c> headings at all is a checkbox plan, and that is the ordinary
    /// case — this repo's own plan.md is one. A file with neither is reported as
    /// <see cref="PlanFormat.Checkbox"/> too, so it produces the familiar "no <c>- [ ]</c> checkboxes"
    /// warning rather than a new and less familiar one.
    /// </para>
    /// <para>
    /// <b>When a file has both, checkboxes win only while they still represent work</b> — that is,
    /// while at least one unticked <c>- [ ]</c> remains. A milestone plan routinely ends with a
    /// residual list of <c>- [x]</c> and <c>- [!]</c> open items that nobody intends as the unit of
    /// progress, and first-match-wins detection read a fully delivered 16-milestone plan as "4 of 13
    /// items complete". Worse than the tally: the resolved format is pinned onto the settings the
    /// runner receives (<c>PilotScheduler</c>), so a misdetection also hands the run checkbox
    /// conventions for a milestone plan.
    /// </para>
    /// </remarks>
    public static PlanFormat Detect(string? planText)
    {
        if (string.IsNullOrEmpty(planText))
        {
            return PlanFormat.Checkbox;
        }

        var sawMilestone = false;

        foreach (var line in ContentLines(planText))
        {
            var checkbox = CheckboxPattern().Match(line);
            if (checkbox.Success)
            {
                // Live work in checkbox form settles it, whatever else the file contains. A ticked or
                // blocked one is only a record of work, so it decides nothing on its own.
                if (checkbox.Groups["mark"].ValueSpan[0] == ' ')
                {
                    return PlanFormat.Checkbox;
                }

                continue;
            }

            if (!sawMilestone && MilestoneHeadingPattern().IsMatch(line))
            {
                sawMilestone = true;
            }
        }

        return sawMilestone ? PlanFormat.Milestone : PlanFormat.Checkbox;
    }

    /// <summary>
    /// Counts <c>- [ ]</c>, <c>- [x]</c> and <c>- [!]</c> the way plan.md §9.1 describes.
    /// </summary>
    /// <remarks>
    /// <c>*</c> and <c>+</c> bullets are accepted alongside <c>-</c> because CommonMark treats them
    /// identically and a user's editor may rewrite them.
    /// </remarks>
    public static PlanItemCounts CountCheckboxes(string? planText)
    {
        if (string.IsNullOrEmpty(planText))
        {
            return PlanItemCounts.Empty;
        }

        var completed = 0;
        var remaining = 0;
        var blocked = 0;

        foreach (var line in ContentLines(planText))
        {
            var match = CheckboxPattern().Match(line);
            if (!match.Success)
            {
                continue;
            }

            switch (match.Groups["mark"].ValueSpan[0])
            {
                case 'x':
                case 'X':
                    completed++;
                    break;
                case '!':
                    blocked++;
                    break;
                default:
                    remaining++;
                    break;
            }
        }

        return new PlanItemCounts(completed, remaining, blocked);
    }

    /// <summary>
    /// Counts <c>### M7 — Title</c> milestones, and how many of them have been delivered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A milestone counts as delivered when its heading says so (<c>— **delivered 2026-07-27**</c>) or
    /// its body opens with a <c>**Status:**</c> or <c>**Post-milestone status …**</c> line. It counts
    /// as blocked when the heading or a body line says <c>**Blocked:**</c> — the milestone equivalent
    /// of <c>- [!]</c>, and the marker a run is told to write when it cannot finish.
    /// </para>
    /// <para>
    /// <b>Then everything numbered below the highest delivered milestone is counted delivered too.</b>
    /// That is deliberate and it is what makes real files read correctly: plans routinely start
    /// marking milestones only once shipping begins, so the early ones carry no marker at all and a
    /// literal reading reports a project that is nearly finished as barely started. Milestones are
    /// numbered because they are ordered, so the highest delivered one is evidence about its
    /// predecessors. An explicit <c>**Blocked:**</c> always wins over the backfill — a milestone
    /// someone deliberately flagged is never quietly counted as done.
    /// </para>
    /// <para>
    /// The heading pattern is anchored on the <c>M&lt;digits&gt;</c> token precisely so ordinary
    /// headings — <c>### Sizing &amp; sequencing notes</c>, <c>#### The catalog</c> — cannot be
    /// mistaken for milestones.
    /// </para>
    /// </remarks>
    public static PlanItemCounts CountMilestones(string? planText)
    {
        if (string.IsNullOrEmpty(planText))
        {
            return PlanItemCounts.Empty;
        }

        var milestones = new List<MilestoneState>();
        MilestoneState? current = null;

        foreach (var line in ContentLines(planText))
        {
            var heading = HeadingPattern().Match(line);
            if (heading.Success)
            {
                var level = heading.Groups["hashes"].Length;
                var milestone = MilestoneHeadingPattern().Match(line);

                // A milestone heading always starts a new one. An ordinary heading only ends the
                // current milestone when it is at the same level or shallower — a `####` subsection
                // inside a `###` milestone is part of that milestone's body.
                if (milestone.Success || level <= (current?.Level ?? int.MaxValue))
                {
                    current = null;
                }

                if (milestone.Success)
                {
                    current = new MilestoneState(
                        int.Parse(milestone.Groups["n"].ValueSpan),
                        level);

                    ReadMarkers(current, milestone.Groups["rest"].Value);
                    milestones.Add(current);
                }

                continue;
            }

            if (current is not null)
            {
                ReadMarkers(current, line);
            }
        }

        return Tally(milestones);

        static void ReadMarkers(MilestoneState milestone, string text)
        {
            var trimmed = text.TrimStart();

            if (trimmed.Contains(BlockedMarker, StringComparison.OrdinalIgnoreCase))
            {
                milestone.IsBlocked = true;
            }

            if (trimmed.Contains(DeliveredMarker, StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith(StatusMarker, StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith(PostMilestoneStatusMarker, StringComparison.OrdinalIgnoreCase))
            {
                milestone.IsDelivered = true;
            }
        }
    }

    /// <summary>Classifies every milestone, applying the backfill rule described on the caller.</summary>
    static PlanItemCounts Tally(List<MilestoneState> milestones)
    {
        var highestDelivered = int.MinValue;
        foreach (var milestone in milestones)
        {
            if (milestone is { IsDelivered: true, IsBlocked: false } && milestone.Number > highestDelivered)
            {
                highestDelivered = milestone.Number;
            }
        }

        var completed = 0;
        var blocked = 0;
        var remaining = 0;

        foreach (var milestone in milestones)
        {
            if (milestone.IsBlocked)
            {
                blocked++;
            }
            else if (milestone.IsDelivered || milestone.Number < highestDelivered)
            {
                completed++;
            }
            else
            {
                remaining++;
            }
        }

        return new PlanItemCounts(completed, remaining, blocked, PlanFormat.Milestone);
    }

    /// <summary>Every line outside a fenced code block, CR-trimmed.</summary>
    static IEnumerable<string> ContentLines(string planText)
    {
        var inFence = false;

        foreach (var raw in planText.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal) ||
                trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                continue;
            }

            yield return line;
        }
    }

    /// <summary>Written into a milestone heading when it ships; the milestone form of <c>- [x]</c>.</summary>
    internal const string DeliveredMarker = "**delivered";

    /// <summary>The milestone form of <c>- [!]</c>.</summary>
    internal const string BlockedMarker = "**blocked";

    /// <summary>A body line that only exists once a milestone has been implemented.</summary>
    internal const string StatusMarker = "**Status:**";

    /// <summary>The variant real plans write, e.g. <c>**Post-milestone status (2026-07-26):**</c>.</summary>
    internal const string PostMilestoneStatusMarker = "**Post-milestone status";

    /// <summary>A markdown task-list item at the start of a line, with any of the three marks.</summary>
    [GeneratedRegex(@"^[ \t]*[-*+][ \t]+\[(?<mark>[ xX!])\](?=[ \t]|\r?$)", RegexOptions.CultureInvariant)]
    internal static partial Regex CheckboxPattern();

    /// <summary>Any ATX heading, so a milestone body knows where it ends.</summary>
    [GeneratedRegex(@"^(?<hashes>\#{1,6})[ \t]+\S", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingPattern();

    /// <summary>
    /// A milestone heading: <c>## M0 …</c> through <c>###### M15 …</c>. The <c>M&lt;digits&gt;</c>
    /// token is mandatory and word-bounded, which is what keeps ordinary headings out.
    /// </summary>
    [GeneratedRegex(@"^(?<hashes>\#{2,6})[ \t]+M(?<n>\d+)\b(?<rest>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex MilestoneHeadingPattern();

    sealed class MilestoneState(int number, int level)
    {
        public int Number { get; } = number;

        public int Level { get; } = level;

        public bool IsDelivered { get; set; }

        public bool IsBlocked { get; set; }
    }
}
