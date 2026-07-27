using NightShift.Core.Configuration;

namespace NightShift.Desktop.Tests;

/// <summary>The Project card's plan block: the click target, and what a save does to the counts.</summary>
public sealed class DashboardPlanCardTests
{
    [Fact]
    public void Opening_the_plan_shows_the_window()
    {
        using var harness = new ViewModelHarness(
            new PilotSettings { ProjectDirectory = @"C:\code\Thing" });

        var dashboard = harness.CreateDashboard();
        dashboard.OpenPlanCommand.Execute(null);

        Assert.Equal(1, harness.PlanWindow.ShowCount);
    }

    [Fact]
    public void The_plan_cannot_be_opened_without_a_project_directory()
    {
        using var harness = new ViewModelHarness();
        var dashboard = harness.CreateDashboard();

        Assert.False(dashboard.OpenPlanCommand.CanExecute(null));
    }

    [Fact]
    public async Task Choosing_a_project_directory_enables_the_plan_button()
    {
        using var harness = new ViewModelHarness();
        var dashboard = harness.CreateDashboard();

        await harness.Settings.SaveAsync(new PilotSettings { ProjectDirectory = @"C:\code\Thing" });

        Assert.True(dashboard.OpenPlanCommand.CanExecute(null));
    }

    [Fact]
    public async Task Saving_the_plan_makes_the_card_recount()
    {
        // Ticking a box in the plan window and watching the card not move is what makes the two
        // look unrelated.
        using var harness = new ViewModelHarness();
        var project = Path.Combine(harness.Root, "project");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "plan.md"), "- [ ] one\n");
        await harness.Settings.SaveAsync(new PilotSettings { ProjectDirectory = project });

        harness.PreflightPasses();
        var dashboard = harness.CreateDashboard();
        var plan = harness.CreatePlan();
        await plan.LoadAsync();

        var before = harness.Preflight.CheckCount;

        plan.BeginEditCommand.Execute(null);
        plan.RawText = "- [x] one\n";
        await plan.SaveCommand.ExecuteAsync(null);

        Assert.Equal(before + 1, harness.Preflight.CheckCount);
    }

    [Fact]
    public async Task A_save_during_a_run_does_not_recount()
    {
        // Preflight reads the same file, and CycleCompleted re-runs it anyway.
        using var harness = new ViewModelHarness();
        var project = Path.Combine(harness.Root, "project");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "plan.md"), "- [ ] one\n");
        await harness.Settings.SaveAsync(new PilotSettings { ProjectDirectory = project });

        var dashboard = harness.CreateDashboard();
        var plan = harness.CreatePlan();
        await plan.LoadAsync();

        using var run = await harness.StartHeldRunAsync();
        Assert.True(dashboard.IsRunning);

        var before = harness.Preflight.CheckCount;

        plan.BeginEditCommand.Execute(null);
        plan.RawText = "- [x] one\n";
        await plan.SaveCommand.ExecuteAsync(null);

        Assert.Equal(before, harness.Preflight.CheckCount);
    }
}
