using NightShift.Core.Configuration;
using NightShift.Core.Markdown;
using NightShift.Core.Preflight;

namespace NightShift.Core.Tests.Markdown;

/// <summary>
/// The reader and the dashboard tally must agree about the same file.
/// </summary>
/// <remarks>
/// If the plan window renders thirty checkboxes beside a card reading "28 items complete", the app
/// is contradicting itself on one screen, and the user has no way to tell which half is lying.
/// Sharing <see cref="MarkdownLines"/> and <see cref="PlanParser"/>'s patterns is what makes that
/// impossible; these tests are what keep it impossible.
/// </remarks>
public sealed class MarkdownAgreesWithPlanParserTests
{
    public static TheoryData<string> PlanFixtures() => new("checkbox-plan.md", "milestone-plan.md");

    static string Load(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    /// <summary>Every task item in the document, at any nesting depth, inside quotes included.</summary>
    static List<TaskMark> TaskMarks(IEnumerable<MarkdownBlock> blocks)
    {
        var marks = new List<TaskMark>();

        foreach (var block in blocks)
        {
            switch (block)
            {
                case ListBlock list:
                    foreach (var item in list.Items)
                    {
                        if (item.Task is { } task)
                        {
                            marks.Add(task);
                        }

                        marks.AddRange(TaskMarks(item.Blocks));
                    }

                    break;

                case QuoteBlock quote:
                    marks.AddRange(TaskMarks(quote.Blocks));
                    break;
            }
        }

        return marks;
    }

    [Theory]
    [MemberData(nameof(PlanFixtures))]
    public void The_task_marks_the_reader_finds_match_the_tally(string fixture)
    {
        var text = Load(fixture);

        var counts = PlanParser.CountCheckboxes(text);
        var marks = TaskMarks(MarkdownReader.Read(text).Blocks);

        Assert.Equal(counts.Completed, marks.Count(mark => mark == TaskMark.Done));
        Assert.Equal(counts.Remaining, marks.Count(mark => mark == TaskMark.Todo));
        Assert.Equal(counts.Blocked, marks.Count(mark => mark == TaskMark.Blocked));
    }

    [Fact]
    public void The_examples_inside_a_fence_are_counted_by_neither()
    {
        // checkbox-plan.md documents its own conventions inside a fence, exactly as this repo's
        // plan does. Both readers must ignore that block, and they must ignore it identically.
        var text = Load("checkbox-plan.md");

        Assert.Contains("- [!] an item that is blocked", text, StringComparison.Ordinal);

        var marks = TaskMarks(MarkdownReader.Read(text).Blocks);

        Assert.Equal(PlanParser.CountCheckboxes(text).Total, marks.Count);
        Assert.Equal(1, marks.Count(mark => mark == TaskMark.Blocked));
    }

    [Fact]
    public void Every_milestone_heading_the_reader_finds_is_one_the_tally_found()
    {
        var text = Load("milestone-plan.md");

        var headings = MarkdownReader.Read(text).Blocks
            .OfType<HeadingBlock>()
            .Where(heading => heading.MilestoneNumber is not null)
            .ToList();

        // Seven milestones, and the "Sizing & sequencing notes" heading is not one of them.
        Assert.Equal([0, 1, 2, 3, 4, 5, 6], headings.Select(heading => heading.MilestoneNumber!.Value));
        Assert.Equal(headings.Count, PlanParser.CountMilestones(text).Total);
    }

    [Fact]
    public void The_headings_marker_agrees_with_what_the_tally_concluded_about_that_heading()
    {
        var text = Load("milestone-plan.md");

        var markers = MarkdownReader.Read(text).Blocks
            .OfType<HeadingBlock>()
            .Where(heading => heading.MilestoneNumber is not null)
            .ToDictionary(heading => heading.MilestoneNumber!.Value, heading => heading.Marker);

        Assert.Equal(MilestoneMarker.Delivered, markers[3]);
        Assert.Equal(MilestoneMarker.None, markers[0]);

        // M4's delivery is stated in the heading; M5's blockage is in its body, which a heading
        // cannot see. That asymmetry is documented on MilestoneMarker and is why the window shows
        // the tally beside the document rather than trying to reconcile the two silently.
        Assert.Equal(MilestoneMarker.Delivered, markers[4]);
        Assert.Equal(MilestoneMarker.None, markers[5]);

        var tally = PlanParser.CountMilestones(text);
        Assert.Equal(1, tally.Blocked);
    }

    [Fact]
    public void The_format_detection_still_sees_each_fixture_as_itself()
    {
        Assert.Equal(PlanFormat.Checkbox, PlanParser.Detect(Load("checkbox-plan.md")));
        Assert.Equal(PlanFormat.Milestone, PlanParser.Detect(Load("milestone-plan.md")));
    }
}
