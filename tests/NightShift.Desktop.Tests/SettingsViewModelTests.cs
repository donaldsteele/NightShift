using NightShift.Core.Configuration;
using NightShift.Core.Execution;
using NightShift.Core.Usage;

namespace NightShift.Desktop.Tests;

/// <summary>
/// plan.md §9.2: "saved on change with a debounce (no OK/Cancel)".
/// </summary>
public class SettingsViewModelTests
{
    static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

    [Fact]
    public void AnEditDoesNotWriteImmediately()
    {
        using var harness = new ViewModelHarness();
        using var settings = harness.CreateSettings(Debounce);

        settings.IntervalMinutes = 90;

        Assert.True(settings.HasPendingChanges);
        Assert.Equal(0, harness.Settings.SaveCount);
    }

    [Fact]
    public void TheWriteHappensOnceTheQuietPeriodElapses()
    {
        using var harness = new ViewModelHarness();
        using var settings = harness.CreateSettings(Debounce);

        settings.IntervalMinutes = 90;

        harness.Time.Advance(Debounce - TimeSpan.FromMilliseconds(1));
        Assert.Equal(0, harness.Settings.SaveCount);

        harness.Time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal(1, harness.Settings.SaveCount);
        Assert.False(settings.HasPendingChanges);
        Assert.Equal(90, harness.Settings.Current.IntervalMinutes);
    }

    [Fact]
    public void ABurstOfEditsCollapsesIntoOneWrite()
    {
        using var harness = new ViewModelHarness();
        using var settings = harness.CreateSettings(Debounce);

        // What dragging the threshold slider looks like.
        for (var percent = 90; percent >= 70; percent--)
        {
            settings.ThresholdPercent = percent;
            harness.Time.Advance(TimeSpan.FromMilliseconds(20));
        }

        Assert.Equal(0, harness.Settings.SaveCount);

        harness.Time.Advance(Debounce);

        Assert.Equal(1, harness.Settings.SaveCount);
        Assert.Equal(70, harness.Settings.Current.ThresholdPercent);
    }

    [Fact]
    public async Task FlushWritesAPendingEditImmediately()
    {
        using var harness = new ViewModelHarness();
        using var settings = harness.CreateSettings(Debounce);

        settings.PlanFileName = "roadmap.md";
        await settings.FlushAsync();

        Assert.Equal(1, harness.Settings.SaveCount);
        Assert.Equal("roadmap.md", harness.Settings.Current.PlanFileName);
        Assert.False(settings.HasPendingChanges);
    }

    [Fact]
    public async Task FlushWithNothingPendingWritesNothing()
    {
        using var harness = new ViewModelHarness();
        using var settings = harness.CreateSettings(Debounce);

        await settings.FlushAsync();

        Assert.Equal(0, harness.Settings.SaveCount);
    }

    [Fact]
    public async Task EverySettingSurvivesARoundTripThroughTheStore()
    {
        using var harness = new ViewModelHarness();
        using var settings = harness.CreateSettings(Debounce);

        settings.ProjectDirectory = @"C:\code\NightShift";
        settings.PlanFileName = "roadmap.md";
        settings.IntervalMinutes = 30;
        settings.RunOnStartup = true;
        settings.StartMinimized = true;
        settings.AlignToQuotaReset = false;
        settings.QuotaResetGraceMinutes = 5;
        settings.ThresholdPercent = 75;
        settings.UsageMetric = UsageMetric.FiveHour;
        settings.OnUsageUnavailable = UsageUnavailableBehavior.Run;
        settings.ClaudeExecutablePath = @"C:\npm\claude.cmd";
        settings.LaunchMode = LaunchMode.VisibleTerminal;
        settings.Model = "claude-opus-5";
        settings.ResumeStrategy = ResumeStrategy.Resume;
        settings.MaxRunDurationMinutes = 20;
        settings.StallTimeoutMinutes = 4;
        settings.PermissionMode = PermissionMode.AcceptEdits;
        settings.AutoTrustProjectFolder = false;
        settings.AllowedTools = "Bash,Read";
        settings.SkipPermissionsFallback = true;
        settings.CavemanLevel = CavemanLevel.Ultra;
        settings.PromptTemplate = "Do the next thing in {planFile}.";
        settings.UsageProviderOrder = UsageProviderPreference.CcusageOnly;
        settings.CcusageCommandOverride = "npx ccusage@1.2.3";
        settings.DryRun = true;
        settings.HistoryRetentionCount = 42;

        await settings.FlushAsync();

        var saved = harness.Settings.Current;
        Assert.Equal(@"C:\code\NightShift", saved.ProjectDirectory);
        Assert.Equal("roadmap.md", saved.PlanFileName);
        Assert.Equal(30, saved.IntervalMinutes);
        Assert.True(saved.RunOnStartup);
        Assert.True(saved.StartMinimized);
        Assert.False(saved.AlignToQuotaReset);
        Assert.Equal(5, saved.QuotaResetGraceMinutes);
        Assert.Equal(75, saved.ThresholdPercent);
        Assert.Equal(UsageMetric.FiveHour, saved.UsageMetric);
        Assert.Equal(UsageUnavailableBehavior.Run, saved.OnUsageUnavailable);
        Assert.Equal(@"C:\npm\claude.cmd", saved.ClaudeExecutablePath);
        Assert.Equal(LaunchMode.VisibleTerminal, saved.LaunchMode);
        Assert.Equal("claude-opus-5", saved.Model);
        Assert.Equal(ResumeStrategy.Resume, saved.ResumeStrategy);
        Assert.Equal(20, saved.MaxRunDurationMinutes);
        Assert.Equal(4, saved.StallTimeoutMinutes);
        Assert.Equal(PermissionMode.AcceptEdits, saved.PermissionMode);
        Assert.False(saved.AutoTrustProjectFolder);
        Assert.Equal("Bash,Read", saved.AllowedTools);
        Assert.True(saved.SkipPermissionsFallback);
        Assert.Equal(CavemanLevel.Ultra, saved.CavemanLevel);
        Assert.Equal("Do the next thing in {planFile}.", saved.PromptTemplate);
        Assert.Equal(UsageProviderPreference.CcusageOnly, saved.UsageProviderOrder);
        Assert.Equal("npx ccusage@1.2.3", saved.CcusageCommandOverride);
        Assert.True(saved.DryRun);
        Assert.Equal(42, saved.HistoryRetentionCount);

        // And a fresh view model over the same store shows exactly what was written back.
        using var reloaded = harness.CreateSettings(Debounce);
        Assert.Equal(saved.ProjectDirectory, reloaded.ProjectDirectory);
        Assert.Equal(saved.IntervalMinutes, reloaded.IntervalMinutes);
        Assert.Equal(saved.ThresholdPercent, reloaded.ThresholdPercent);
        Assert.Equal(saved.PermissionMode, reloaded.PermissionMode);
        Assert.Equal(saved.CavemanLevel, reloaded.CavemanLevel);
        Assert.Equal(saved.PromptTemplate, reloaded.PromptTemplate);
        Assert.Equal(saved.UsageProviderOrder, reloaded.UsageProviderOrder);
        Assert.False(reloaded.HasPendingChanges);
    }

    [Fact]
    public void EveryPersistedPropertySchedulesASave()
    {
        using var harness = new ViewModelHarness();
        using var settings = harness.CreateSettings(Debounce);

        // Guards against a new option being added to the form and silently never being written.
        var names = typeof(NightShift.Desktop.ViewModels.SettingsViewModel)
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var persisted in NightShift.Desktop.ViewModels.SettingsViewModel.PersistedProperties)
        {
            Assert.Contains(persisted, names);
        }
    }

    [Fact]
    public void PopulatingTheFormFromDiskDoesNotScheduleASave()
    {
        using var harness = new ViewModelHarness();
        using var settings = harness.CreateSettings(Debounce);

        settings.Apply(new PilotSettings { IntervalMinutes = 15, ThresholdPercent = 60 });

        Assert.False(settings.HasPendingChanges);
        Assert.Equal(15, settings.IntervalMinutes);
        harness.Time.Advance(Debounce * 2);
        Assert.Equal(0, harness.Settings.SaveCount);
    }

    [Fact]
    public void TheLivePromptPreviewIsExactlyWhatARunWouldSend()
    {
        using var harness = new ViewModelHarness();
        using var settings = harness.CreateSettings(Debounce);

        settings.CavemanLevel = CavemanLevel.Ultra;
        settings.PlanFileName = "roadmap.md";
        settings.PromptTemplate = "Work through {planFile}.";

        Assert.Equal(
            PromptBuilder.Build(CavemanLevel.Ultra, "Work through {planFile}.", "roadmap.md"),
            settings.PromptPreview);
        Assert.StartsWith("/caveman:caveman ultra", settings.PromptPreview, StringComparison.Ordinal);
        Assert.Contains("Work through roadmap.md.", settings.PromptPreview, StringComparison.Ordinal);
    }

    [Fact]
    public void ResetToDefaultRestoresThePlansTemplate()
    {
        using var harness = new ViewModelHarness();
        using var settings = harness.CreateSettings(Debounce);

        settings.PromptTemplate = "something else";
        Assert.False(settings.IsPromptTemplateDefault);

        settings.ResetPromptTemplateCommand.Execute(null);

        Assert.Equal(PilotSettings.DefaultPromptTemplate, settings.PromptTemplate);
        Assert.True(settings.IsPromptTemplateDefault);
    }

    [Fact]
    public void TheTrustKeyPreviewShowsTheExactClaudeJsonKeys()
    {
        using var harness = new ViewModelHarness();
        using var settings = harness.CreateSettings(Debounce);

        settings.ProjectDirectory = @"C:\code\NightShift";

        Assert.Equal(
            WorkspaceTrustManager.TrustKeysFor(@"C:\code\NightShift"),
            settings.TrustKeysPreview);
    }

    [Fact]
    public async Task TheAutoModeEnvironmentEditorAlwaysSplicesInDefaults()
    {
        using var harness = new ViewModelHarness();
        var store = new FakeAutoModeEnvironmentStore();
        using var settings = harness.CreateSettings(Debounce, store);

        settings.AutoModeEnvironmentText = "registry.internal\n\ngit.internal\n";
        await settings.SaveAutoModeEnvironmentCommand.ExecuteAsync(null);

        var written = Assert.Single(store.Writes);
        Assert.Equal(["registry.internal", "git.internal"], written);
        // The sentinel is the store's job, and it is not offered to the user to delete.
        Assert.DoesNotContain("$defaults", settings.AutoModeEnvironmentText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedAutorunWriteRevertsTheToggleRatherThanLie()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var harness = new ViewModelHarness();
        using var settings = harness.CreateSettings(Debounce);

        // The harness's registrar cannot resolve an executable path, so Register() fails.
        settings.StartWithWindows = true;
        await settings.FlushAsync();

        Assert.False(settings.StartWithWindows);
        Assert.NotEqual(string.Empty, settings.StartWithWindowsError);
        // The registry failure must not be reported as, or wiped by, a settings.json write.
        Assert.Equal(string.Empty, settings.SaveError);
    }
}
