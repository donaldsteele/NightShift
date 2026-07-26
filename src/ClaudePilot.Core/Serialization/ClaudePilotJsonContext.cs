using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudePilot.Core.Configuration;
using ClaudePilot.Core.History;

namespace ClaudePilot.Core.Serialization;

/// <summary>
/// Serializer context for files a human may open and edit — indented, comment- and
/// trailing-comma-tolerant. Reflection-based serialization is avoided throughout so trimming and
/// AOT stay open (plan.md §2).
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(PilotSettings))]
public sealed partial class ClaudePilotJsonContext : JsonSerializerContext;

/// <summary>
/// Serializer context for newline-delimited JSON. <c>WriteIndented</c> must stay false: one record
/// per line is the whole contract of <c>runs/index.jsonl</c>.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RunRecord))]
public sealed partial class ClaudePilotJsonLinesContext : JsonSerializerContext;
