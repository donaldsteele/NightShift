using System.Text.Json;
using ClaudePilot.Core.Configuration;
using ClaudePilot.Core.Usage;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudePilot.Core.Tests.Configuration;

public sealed class JsonSettingsStoreTests : IDisposable
{
    readonly TempDirectory _temp = new();
    readonly AppPaths _paths;

    public JsonSettingsStoreTests() => _paths = new AppPaths(_temp.Path);

    JsonSettingsStore CreateStore() => new(_paths, NullLogger<JsonSettingsStore>.Instance);

    [Fact]
    public async Task Load_on_a_fresh_machine_writes_defaults()
    {
        var store = CreateStore();

        var loaded = await store.LoadAsync();

        Assert.Equal(new PilotSettings(), loaded);
        Assert.True(File.Exists(_paths.SettingsFile));
        Assert.Equal(loaded, store.Current);
    }

    [Fact]
    public async Task Every_setting_round_trips()
    {
        var settings = new PilotSettings
        {
            ProjectDirectory = @"C:\src\some project",
            PlanFileName = "roadmap.md",
            IntervalMinutes = 30,
            RunOnStartup = true,
            StartWithWindows = true,
            StartMinimized = true,
            ThresholdPercent = 75.5,
            UsageMetric = UsageMetric.SevenDay,
            OnUsageUnavailable = UsageUnavailableBehavior.Run,
            ClaudeExecutablePath = @"C:\tools\claude.cmd",
            LaunchMode = LaunchMode.VisibleTerminal,
            Model = "claude-opus-5",
            ResumeStrategy = ResumeStrategy.Resume,
            MaxRunDurationMinutes = 20,
            StallTimeoutMinutes = 3,
            PermissionMode = PermissionMode.BypassPermissions,
            AutoTrustProjectFolder = false,
            AllowedTools = "Bash,Read",
            SkipPermissionsFallback = true,
            CavemanLevel = CavemanLevel.WenyanUltra,
            PromptTemplate = "do the thing in {planFile}",
            UsageProviderOrder = UsageProviderPreference.CcusageThenOAuth,
            CcusageCommandOverride = "npx ccusage@1.2.3 blocks --active --json",
            DryRun = true,
            HistoryRetentionCount = 7,
        };

        await CreateStore().SaveAsync(settings);
        var reloaded = await CreateStore().LoadAsync();

        Assert.Equal(settings, reloaded);
    }

    [Theory]
    [InlineData(UsageMetric.FiveHour)]
    [InlineData(UsageMetric.SevenDay)]
    [InlineData(UsageMetric.HighestOfAll)]
    public async Task Enums_round_trip_as_names(UsageMetric metric)
    {
        await CreateStore().SaveAsync(new PilotSettings { UsageMetric = metric });

        var json = await File.ReadAllTextAsync(_paths.SettingsFile);

        Assert.Contains($"\"{metric}\"", json, StringComparison.Ordinal);
        Assert.Equal(metric, (await CreateStore().LoadAsync()).UsageMetric);
    }

    [Fact]
    public async Task Corrupt_file_is_backed_up_and_defaults_restored()
    {
        _paths.EnsureCreated();
        await File.WriteAllTextAsync(_paths.SettingsFile, "{{{ not json");

        var loaded = await CreateStore().LoadAsync();

        Assert.Equal(new PilotSettings(), loaded);
        Assert.True(File.Exists(_paths.SettingsFile + JsonSettingsStore.CorruptBackupSuffix));
        Assert.Equal("{{{ not json", await File.ReadAllTextAsync(_paths.SettingsFile + JsonSettingsStore.CorruptBackupSuffix));

        // The rewritten file must itself be valid.
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(_paths.SettingsFile));
        Assert.Equal(PilotSettings.CurrentVersion, document.RootElement.GetProperty("settingsVersion").GetInt32());
    }

    [Fact]
    public async Task Repeated_corruption_does_not_overwrite_the_first_backup()
    {
        _paths.EnsureCreated();

        await File.WriteAllTextAsync(_paths.SettingsFile, "first garbage");
        await CreateStore().LoadAsync();
        await File.WriteAllTextAsync(_paths.SettingsFile, "second garbage");
        await CreateStore().LoadAsync();

        Assert.Equal("first garbage", await File.ReadAllTextAsync(_paths.SettingsFile + ".bad"));
        Assert.Equal("second garbage", await File.ReadAllTextAsync(_paths.SettingsFile + ".bad-1"));
    }

    [Fact]
    public async Task Unknown_and_missing_properties_are_tolerated()
    {
        _paths.EnsureCreated();
        await File.WriteAllTextAsync(
            _paths.SettingsFile,
            """
            {
              "settingsVersion": 1,
              "intervalMinutes": 45,
              "somethingFromAFutureVersion": { "nested": [1, 2, 3] }
            }
            """);

        var loaded = await CreateStore().LoadAsync();

        Assert.Equal(45, loaded.IntervalMinutes);
        Assert.Equal(90, loaded.ThresholdPercent);            // default filled in
        Assert.Equal(UsageMetric.HighestOfAll, loaded.UsageMetric);
        Assert.False(File.Exists(_paths.SettingsFile + JsonSettingsStore.CorruptBackupSuffix));
    }

    [Fact]
    public async Task Out_of_range_values_are_clamped_on_load()
    {
        _paths.EnsureCreated();
        await File.WriteAllTextAsync(
            _paths.SettingsFile,
            """
            { "intervalMinutes": 99999, "thresholdPercent": 400, "historyRetentionCount": 0, "allowedTools": "" }
            """);

        var loaded = await CreateStore().LoadAsync();

        Assert.Equal(PilotSettings.MaxIntervalMinutes, loaded.IntervalMinutes);
        Assert.Equal(PilotSettings.MaxThresholdPercent, loaded.ThresholdPercent);
        Assert.Equal(1, loaded.HistoryRetentionCount);
        Assert.Equal(PilotSettings.DefaultAllowedTools, loaded.AllowedTools);
    }

    [Fact]
    public async Task Older_schema_version_is_migrated_and_rewritten()
    {
        _paths.EnsureCreated();
        await File.WriteAllTextAsync(_paths.SettingsFile, """{ "settingsVersion": 0, "intervalMinutes": 15 }""");

        var loaded = await CreateStore().LoadAsync();

        Assert.Equal(PilotSettings.CurrentVersion, loaded.SettingsVersion);
        Assert.Equal(15, loaded.IntervalMinutes);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(_paths.SettingsFile));
        Assert.Equal(PilotSettings.CurrentVersion, document.RootElement.GetProperty("settingsVersion").GetInt32());
    }

    [Fact]
    public async Task Save_raises_the_change_event()
    {
        var store = CreateStore();
        PilotSettings? observed = null;
        store.SettingsChanged += (_, s) => observed = s;

        await store.SaveAsync(new PilotSettings { IntervalMinutes = 17 });

        Assert.NotNull(observed);
        Assert.Equal(17, observed.IntervalMinutes);
        Assert.Equal(17, store.Current.IntervalMinutes);
    }

    [Fact]
    public async Task Concurrent_saves_never_leave_a_torn_file()
    {
        var store = CreateStore();

        await Task.WhenAll(Enumerable.Range(PilotSettings.MinIntervalMinutes, 40)
            .Select(i => store.SaveAsync(new PilotSettings { IntervalMinutes = i })));

        var json = await File.ReadAllTextAsync(_paths.SettingsFile);
        using var document = JsonDocument.Parse(json);
        var written = document.RootElement.GetProperty("intervalMinutes").GetInt32();

        Assert.InRange(written, PilotSettings.MinIntervalMinutes, PilotSettings.MinIntervalMinutes + 39);
        Assert.Empty(Directory.GetFiles(_paths.Root, "*.tmp"));
    }

    public void Dispose() => _temp.Dispose();
}
