using System.Text.Json;
using NightShift.Core.Configuration;
using NightShift.Core.Serialization;

namespace NightShift.Core.Tests.Serialization;

public sealed class PilotSettingsSerializationTests
{
    [Fact]
    public void Absent_properties_keep_their_declared_defaults()
    {
        var settings = JsonSerializer.Deserialize("{}", NightShiftJsonContext.Default.PilotSettings);

        Assert.NotNull(settings);
        Assert.Equal(PilotSettings.CurrentVersion, settings.SettingsVersion);
        Assert.Equal(60, settings.IntervalMinutes);
        Assert.Equal(90, settings.ThresholdPercent);
        Assert.Equal(UsageMetric.HighestOfAll, settings.UsageMetric);
        Assert.Equal(PermissionMode.Auto, settings.PermissionMode);
        Assert.Equal(CavemanLevel.Full, settings.CavemanLevel);
        Assert.True(settings.AutoTrustProjectFolder);
        Assert.Equal(PilotSettings.DefaultAllowedTools, settings.AllowedTools);
        Assert.Equal(PilotSettings.DefaultPromptTemplate, settings.PromptTemplate);
    }
}
