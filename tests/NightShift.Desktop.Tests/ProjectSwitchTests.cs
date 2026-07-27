using NightShift.Core.Configuration;
using NightShift.Core.Preflight;

namespace NightShift.Desktop.Tests;

/// <summary>
/// Pointing the app at a different repository must re-read that repository's plan.
/// </summary>
/// <remarks>
/// Nothing re-ran preflight when settings changed: <c>ApplySettings</c> copied labels and stopped,
/// and the only other triggers were a scheduler cycle, a preflight fix, and the manual button. So
/// switching projects left the previous one's tally, pills and plan rows on screen — one project's
/// progress reported under another project's name — until the next cycle came round.
/// </remarks>
public sealed class ProjectSwitchTests
{
    static PreflightResult WithCounts(PlanItemCounts counts) =>
        new([new PreflightCheck(PreflightCheckId.PlanItems, "Plan", PreflightStatus.Ok, "ok")],
            DateTimeOffset.UnixEpoch,
            counts);

    [Fact]
    public async Task Changing_the_project_directory_re_reads_the_plan()
    {
        using var harness = new ViewModelHarness(
            new PilotSettings { ProjectDirectory = @"C:\code\First" });

        harness.Preflight.Result = WithCounts(new PlanItemCounts(12, 18, 0));
        var dashboard = harness.CreateDashboard();
        await dashboard.InitializeAsync();

        Assert.Equal("12 of 30 items complete", dashboard.PlanItemsSummary);

        harness.Preflight.Result = WithCounts(new PlanItemCounts(3, 1, 0));
        await harness.Settings.SaveAsync(new PilotSettings { ProjectDirectory = @"C:\code\Second" });

        Assert.Equal(@"C:\code\Second", dashboard.ProjectDirectory);
        Assert.Equal("3 of 4 items complete", dashboard.PlanItemsSummary);
    }

    [Fact]
    public async Task Changing_the_plan_file_name_re_reads_it_too()
    {
        using var harness = new ViewModelHarness(
            new PilotSettings { ProjectDirectory = @"C:\code\Thing", PlanFileName = "plan.md" });

        harness.Preflight.Result = WithCounts(new PlanItemCounts(12, 18, 0));
        var dashboard = harness.CreateDashboard();
        await dashboard.InitializeAsync();

        harness.Preflight.Result = WithCounts(new PlanItemCounts(0, 7, 0));
        await harness.Settings.SaveAsync(
            new PilotSettings { ProjectDirectory = @"C:\code\Thing", PlanFileName = "roadmap.md" });

        Assert.Equal("0 of 7 items complete", dashboard.PlanItemsSummary);
    }

    [Fact]
    public async Task Changing_the_plan_format_re_reads_it_too()
    {
        using var harness = new ViewModelHarness(
            new PilotSettings { ProjectDirectory = @"C:\code\Thing" });

        harness.Preflight.Result = WithCounts(new PlanItemCounts(12, 18, 0));
        var dashboard = harness.CreateDashboard();
        await dashboard.InitializeAsync();

        var before = harness.Preflight.CheckCount;

        await harness.Settings.SaveAsync(new PilotSettings
        {
            ProjectDirectory = @"C:\code\Thing",
            PlanFormat = PlanFormat.Milestone,
        });

        Assert.Equal(before + 1, harness.Preflight.CheckCount);
    }

    [Fact]
    public async Task A_settings_change_that_does_not_touch_the_plan_does_not_re_read_it()
    {
        // The threshold, the metric and everything else on the Settings screen save on a debounce
        // as the user types. Re-running preflight on each keystroke would be a scan per character.
        using var harness = new ViewModelHarness(
            new PilotSettings { ProjectDirectory = @"C:\code\Thing" });

        var dashboard = harness.CreateDashboard();
        await dashboard.InitializeAsync();

        var before = harness.Preflight.CheckCount;

        await harness.Settings.SaveAsync(new PilotSettings
        {
            ProjectDirectory = @"C:\code\Thing",
            ThresholdPercent = 55,
        });

        Assert.Equal(before, harness.Preflight.CheckCount);
    }

    [Fact]
    public void Starting_up_does_not_scan_twice()
    {
        // The constructor applies settings before InitializeAsync establishes the first preflight.
        // Treating that first application as a change would scan the plan twice on every launch.
        using var harness = new ViewModelHarness(
            new PilotSettings { ProjectDirectory = @"C:\code\Thing" });

        _ = harness.CreateDashboard();

        Assert.Equal(0, harness.Preflight.CheckCount);
    }

    [Fact]
    public async Task The_plan_window_follows_the_project_and_drops_an_edit_to_the_old_one()
    {
        // Otherwise the window shows the previous project's plan under the new project's name, and
        // worse, a save would write the old project's text into the new project's file.
        using var harness = new ViewModelHarness();

        var first = Path.Combine(harness.Root, "first");
        var second = Path.Combine(harness.Root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(first, "plan.md"), "# First\n\n- [ ] one\n");
        File.WriteAllText(Path.Combine(second, "plan.md"), "# Second\n\n- [x] done\n");

        await harness.Settings.SaveAsync(new PilotSettings { ProjectDirectory = first });

        var plan = harness.CreatePlan();
        await plan.LoadAsync();

        plan.BeginEditCommand.Execute(null);
        plan.RawText = "# Edited the first one\n";

        await harness.Settings.SaveAsync(new PilotSettings { ProjectDirectory = second });
        await plan.PendingLoad;

        Assert.Equal(Path.Combine(second, "plan.md"), plan.PlanPath);
        Assert.Equal("# Second\n\n- [x] done\n", plan.RawText);
        Assert.False(plan.IsEditing);
        Assert.Equal(Path.Combine(second, "plan.md"), harness.Watcher.Watching);
    }
}
