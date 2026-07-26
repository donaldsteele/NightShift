using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudePilot.Core.Configuration;

namespace ClaudePilot.Core.Serialization;

/// <summary>
/// Source-generated serializer contexts for everything ClaudePilot persists. Reflection-based
/// serialization is avoided so trimming and AOT stay open (plan.md §2).
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(PilotSettings))]
public sealed partial class ClaudePilotJsonContext : JsonSerializerContext;
