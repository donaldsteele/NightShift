using System.Text.Json.Serialization;

namespace NightShift.Core.Usage;

/// <summary>Which provider produced a usage snapshot (plan.md §4).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UsageSource>))]
public enum UsageSource
{
    /// <summary>The undocumented Anthropic OAuth usage endpoint — a true quota percentage.</summary>
    OAuth,

    /// <summary>`ccusage` reading local transcripts — token-derived, always approximate.</summary>
    Ccusage,

    /// <summary>No provider succeeded.</summary>
    None,
}

/// <summary>
/// Which providers <c>CompositeUsageProvider</c> consults, and in what order (plan.md §4.3, §9.2
/// "Advanced — usage provider order"). Modelled as a single value rather than a list so
/// <see cref="Configuration.PilotSettings"/> keeps record value equality — the scheduler compares
/// whole settings objects to decide what to restart.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<UsageProviderPreference>))]
public enum UsageProviderPreference
{
    /// <summary>The real quota percentage first, token-derived estimate as fallback. Default.</summary>
    OAuthThenCcusage,

    /// <summary>Estimate first. For accounts where the OAuth endpoint is persistently rate-limited.</summary>
    CcusageThenOAuth,

    /// <summary>Never fall back to an approximation.</summary>
    OAuthOnly,

    /// <summary>Never call the undocumented endpoint.</summary>
    CcusageOnly,
}

public static class UsageProviderPreferenceExtensions
{
    /// <summary>The providers to try, most preferred first.</summary>
    public static IReadOnlyList<UsageSource> ToSequence(this UsageProviderPreference preference) =>
        preference switch
        {
            UsageProviderPreference.OAuthThenCcusage => [UsageSource.OAuth, UsageSource.Ccusage],
            UsageProviderPreference.CcusageThenOAuth => [UsageSource.Ccusage, UsageSource.OAuth],
            UsageProviderPreference.OAuthOnly => [UsageSource.OAuth],
            UsageProviderPreference.CcusageOnly => [UsageSource.Ccusage],
            _ => throw new ArgumentOutOfRangeException(
                nameof(preference), preference, "Unknown usage provider preference"),
        };
}
