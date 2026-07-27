using NightShift.Core.Configuration;

namespace NightShift.Core.Tests.Configuration;

public sealed class PilotSettingsPlanPathTests
{
    [Fact]
    public void The_path_is_the_directory_and_the_file_name()
    {
        var settings = new PilotSettings { ProjectDirectory = @"C:\code\Thing", PlanFileName = "plan.md" };

        Assert.Equal(Path.Combine(@"C:\code\Thing", "plan.md"), settings.ResolvePlanPath());
    }

    [Fact]
    public void A_relative_plan_file_name_is_allowed()
    {
        // `docs/plan.md` is legitimate: the plan need not sit at the root of the project.
        var settings = new PilotSettings { ProjectDirectory = @"C:\code\Thing", PlanFileName = "docs/plan.md" };

        Assert.Equal(Path.Combine(@"C:\code\Thing", "docs/plan.md"), settings.ResolvePlanPath());
    }

    [Fact]
    public void No_project_directory_means_no_path() =>
        Assert.Null(new PilotSettings { ProjectDirectory = string.Empty }.ResolvePlanPath());

    [Fact]
    public void A_whitespace_project_directory_means_no_path() =>
        Assert.Null(new PilotSettings { ProjectDirectory = "   " }.ResolvePlanPath());

    [Fact]
    public void A_blank_plan_file_name_falls_back_to_the_default()
    {
        // Normalized() replaces a blank name with plan.md, so the instance overload never sees one.
        var settings = new PilotSettings { ProjectDirectory = @"C:\code\Thing", PlanFileName = "  " };

        Assert.Equal(Path.Combine(@"C:\code\Thing", "plan.md"), settings.ResolvePlanPath());
    }

    [Theory]
    [InlineData("plan\0.md")]
    [InlineData("plan\n.md")]
    [InlineData("*.md")]
    [InlineData("plan?.md")]
    public void An_unusable_file_name_gives_null_rather_than_a_path(string planFileName)
    {
        // The name is free text from the settings box. Path.Combine used to throw on invalid
        // characters and .NET Core stopped doing so, which is why the check is explicit.
        Assert.Null(PilotSettings.ResolvePlanPath(@"C:\code\Thing", planFileName));
    }

    [Fact]
    public void The_static_overload_agrees_with_the_instance_one()
    {
        var settings = new PilotSettings { ProjectDirectory = @"C:\code\Thing", PlanFileName = " plan.md " };

        Assert.Equal(
            PilotSettings.ResolvePlanPath(@"C:\code\Thing", "plan.md"),
            settings.ResolvePlanPath());
    }
}
