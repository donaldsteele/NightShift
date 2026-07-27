using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NightShift.Core.Io;

namespace NightShift.Desktop.Services;

/// <param name="Entries">The environment entries, excluding the <c>"$defaults"</c> sentinel.</param>
/// <param name="Error">Non-null when the file exists but could not be read or parsed.</param>
public sealed record AutoModeEnvironment(IReadOnlyList<string> Entries, string? Error = null)
{
    public static AutoModeEnvironment Empty { get; } = new([]);
}

/// <summary>
/// The collapsed <c>autoMode.environment</c> editor from plan.md §9.2 Advanced / §5.3.2.
/// </summary>
/// <remarks>
/// This is the one Settings control that does not write <see cref="NightShift.Core.Configuration.PilotSettings"/>:
/// the value belongs to Claude Code, in <c>~/.claude/settings.json</c>. Core owns no writer for it,
/// so it lives here.
/// </remarks>
public interface IAutoModeEnvironmentStore
{
    /// <summary>The file being edited, so the UI can name it.</summary>
    string SettingsPath { get; }

    Task<AutoModeEnvironment> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces <c>autoMode.environment</c> with <paramref name="entries"/>, always splicing in the
    /// literal <c>"$defaults"</c>. Returns null on success, or a message on failure.
    /// </summary>
    Task<string?> WriteAsync(IReadOnlyList<string> entries, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads and writes <c>autoMode.environment</c> in <c>~/.claude/settings.json</c> through a
/// <see cref="JsonNode"/> round-trip, so every unrelated key the user has survives untouched.
/// </summary>
/// <remarks>
/// <b><c>"$defaults"</c> is spliced in unconditionally and cannot be removed from the UI.</b>
/// plan.md §5.3.2 is explicit that writing this list without it silently discards every built-in
/// classifier rule — the exact failure mode that turns an unattended run loose with no safety net.
/// The editor therefore never shows the sentinel and always writes it first.
/// </remarks>
public sealed class ClaudeUserSettingsAutoModeEnvironmentStore : IAutoModeEnvironmentStore
{
    /// <summary>The literal that preserves Claude Code's built-in rules.</summary>
    public const string DefaultsSentinel = "$defaults";

    const string AutoModeProperty = "autoMode";
    const string EnvironmentProperty = "environment";

    static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    readonly ILogger<ClaudeUserSettingsAutoModeEnvironmentStore> _logger;

    /// <param name="settingsPath">Overridable so tests never touch the developer's own settings.</param>
    public ClaudeUserSettingsAutoModeEnvironmentStore(
        ILogger<ClaudeUserSettingsAutoModeEnvironmentStore> logger,
        string? settingsPath = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        SettingsPath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(settingsPath) ? DefaultSettingsPath : settingsPath);
    }

    /// <summary><c>~/.claude/settings.json</c>.</summary>
    public static string DefaultSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude",
        "settings.json");

    public string SettingsPath { get; }

    public async Task<AutoModeEnvironment> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SettingsPath))
        {
            return AutoModeEnvironment.Empty;
        }

        JsonObject? root;
        try
        {
            var json = await File.ReadAllTextAsync(SettingsPath, cancellationToken).ConfigureAwait(false);
            root = JsonNode.Parse(json, nodeOptions: null, DocumentOptions) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Could not read {Path}.", SettingsPath);
            return new AutoModeEnvironment([], ex.Message);
        }

        if (root?[AutoModeProperty] is not JsonObject autoMode ||
            autoMode[EnvironmentProperty] is not JsonArray environment)
        {
            return AutoModeEnvironment.Empty;
        }

        var entries = environment
            .OfType<JsonValue>()
            .Select(value => value.TryGetValue<string>(out var text) ? text : null)
            .Where(text => text is { Length: > 0 } &&
                           !string.Equals(text, DefaultsSentinel, StringComparison.Ordinal))
            .Select(text => text!)
            .ToArray();

        return new AutoModeEnvironment(entries);
    }

    public async Task<string?> WriteAsync(
        IReadOnlyList<string> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);

        JsonObject root;
        try
        {
            root = File.Exists(SettingsPath)
                ? JsonNode.Parse(
                      await File.ReadAllTextAsync(SettingsPath, cancellationToken).ConfigureAwait(false),
                      nodeOptions: null,
                      DocumentOptions) as JsonObject ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Could not read {Path} before writing autoMode.environment.", SettingsPath);
            return ex.Message;
        }

        if (root[AutoModeProperty] is not JsonObject autoMode)
        {
            autoMode = [];
            root[AutoModeProperty] = autoMode;
        }

        var array = new JsonArray(JsonValue.Create(DefaultsSentinel));
        foreach (var entry in entries)
        {
            var trimmed = entry?.Trim();
            if (trimmed is { Length: > 0 } &&
                !string.Equals(trimmed, DefaultsSentinel, StringComparison.Ordinal))
            {
                array.Add(JsonValue.Create(trimmed));
            }
        }

        autoMode[EnvironmentProperty] = array;

        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (directory is { Length: > 0 })
            {
                Directory.CreateDirectory(directory);
            }

            await AtomicFile
                .WriteAllTextAsync(SettingsPath, root.ToJsonString(WriteOptions), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not write {Path}.", SettingsPath);
            return ex.Message;
        }

        _logger.LogInformation("Wrote autoMode.environment ({Count} entries) to {Path}.", entries.Count, SettingsPath);
        return null;
    }
}
