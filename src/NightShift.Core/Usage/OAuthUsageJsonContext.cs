using System.Text.Json;
using System.Text.Json.Serialization;

namespace NightShift.Core.Usage;

/// <summary>
/// One rate-limit window as the OAuth usage endpoint sends it (plan.md §4.1).
/// </summary>
/// <remarks>
/// Properties are <c>get; set;</c>, never <c>get; init;</c> — the System.Text.Json source generator
/// compiles <c>init</c> into a parameterized-construction path that passes <c>default(T)</c> for
/// every absent property, which is precisely the "missing field" case this endpoint hands us.
/// See the remarks on <see cref="Configuration.PilotSettings"/>.
/// <para>
/// <c>ResetsAt</c> is deliberately a <see cref="string"/>: the endpoint is undocumented, and a
/// timestamp we cannot parse must cost us the reset time only, not the whole snapshot.
/// </para>
/// </remarks>
internal sealed record OAuthUsageWindowDto
{
    /// <summary>Percentage 0–100. Null when the endpoint sent the field as null or omitted it.</summary>
    [JsonPropertyName("utilization")]
    public double? Utilization { get; set; }

    /// <summary>ISO-8601 instant, e.g. <c>2026-04-11T07:00:00.528743+00:00</c>.</summary>
    [JsonPropertyName("resets_at")]
    public string? ResetsAt { get; set; }
}

/// <summary>
/// The documented-by-observation body of <c>GET /api/oauth/usage</c> (plan.md §4.1). Any window may
/// be <c>null</c> or missing; unknown sibling properties (<c>extra_usage</c> and whatever Anthropic
/// adds next) are ignored by construction.
/// </summary>
internal sealed record OAuthUsageResponseDto
{
    [JsonPropertyName("five_hour")]
    public OAuthUsageWindowDto? FiveHour { get; set; }

    [JsonPropertyName("seven_day")]
    public OAuthUsageWindowDto? SevenDay { get; set; }

    [JsonPropertyName("seven_day_opus")]
    public OAuthUsageWindowDto? SevenDayOpus { get; set; }

    [JsonPropertyName("seven_day_sonnet")]
    public OAuthUsageWindowDto? SevenDaySonnet { get; set; }
}

/// <summary>
/// Serializer context for the OAuth usage endpoint alone (plan.md §4.1). Kept separate from
/// <c>NightShiftJsonContext</c> because these DTOs describe a third-party wire format that can
/// change without notice, while that context describes files we own; the two must be free to adopt
/// different naming and tolerance settings. Reflection-based serialization is avoided throughout so
/// trimming and AOT stay open (plan.md §2).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Disallow,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(OAuthUsageResponseDto))]
internal sealed partial class OAuthUsageJsonContext : JsonSerializerContext;
