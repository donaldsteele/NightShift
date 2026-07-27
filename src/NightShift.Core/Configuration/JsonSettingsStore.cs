using System.Text.Json;
using NightShift.Core.Io;
using NightShift.Core.Serialization;
using Microsoft.Extensions.Logging;

namespace NightShift.Core.Configuration;

/// <summary>
/// Settings persisted as JSON at <c>%APPDATA%\NightShift\settings.json</c>, written atomically.
/// A file that cannot be parsed is moved aside and defaults are restored — the app must start
/// even when its own config is garbage.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    /// <summary>Suffix used when a corrupt settings file is quarantined.</summary>
    public const string CorruptBackupSuffix = ".bad";

    readonly AppPaths _paths;
    readonly ILogger<JsonSettingsStore> _logger;
    readonly SemaphoreSlim _gate = new(1, 1);

    PilotSettings _current = new();

    public JsonSettingsStore(AppPaths paths, ILogger<JsonSettingsStore> logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public PilotSettings Current => Volatile.Read(ref _current);

    public event EventHandler<PilotSettings>? SettingsChanged;

    public async Task<PilotSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (settings, needsRewrite) = await ReadOrRecoverAsync(cancellationToken).ConfigureAwait(false);

            if (needsRewrite)
            {
                await WriteAsync(settings, cancellationToken).ConfigureAwait(false);
            }

            Publish(settings);
            return settings;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(PilotSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var normalized = settings.Normalized() with { SettingsVersion = PilotSettings.CurrentVersion };

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAsync(normalized, cancellationToken).ConfigureAwait(false);
            Publish(normalized);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns the settings to use and whether the file on disk needs rewriting (because it was
    /// missing, corrupt, or written by an older schema version).
    /// </summary>
    async Task<(PilotSettings Settings, bool NeedsRewrite)> ReadOrRecoverAsync(CancellationToken cancellationToken)
    {
        var file = _paths.SettingsFile;

        if (!File.Exists(file))
        {
            _logger.LogInformation("No settings file at {Path}; starting from defaults.", file);
            return (new PilotSettings(), true);
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable is not the same as corrupt — do not destroy a file we merely could not
            // open (a lock, a permissions blip). Run on defaults this time and leave it alone.
            _logger.LogError(ex, "Could not read settings file {Path}; using defaults for this session.", file);
            return (new PilotSettings(), false);
        }

        PilotSettings? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(json, NightShiftJsonContext.Default.PilotSettings);
        }
        catch (JsonException ex)
        {
            return (Quarantine(file, ex), true);
        }

        if (parsed is null)
        {
            return (Quarantine(file, reason: null), true);
        }

        // Compare the version *as written* — Normalized() must not paper over an old schema, or
        // the migration below would never fire and the stale file would stay on disk.
        var fileVersion = parsed.SettingsVersion;
        var normalized = parsed.Normalized();
        var needsRewrite = fileVersion < PilotSettings.CurrentVersion;

        if (needsRewrite)
        {
            _logger.LogInformation(
                "Migrating settings from version {From} to {To}.",
                fileVersion,
                PilotSettings.CurrentVersion);

            normalized = Migrate(normalized, fileVersion) with { SettingsVersion = PilotSettings.CurrentVersion };
        }

        return (normalized, needsRewrite);
    }

    /// <summary>
    /// Schema upgrades that need more than a version bump.
    /// </summary>
    /// <remarks>
    /// <b>v1 → v2: the prompt template.</b> v2's default template carries a
    /// <c>{planConventions}</c> token, which is how a run learns whether to tick checkboxes or mark
    /// milestones delivered. A settings file written by v1 holds the whole v1 default as a literal
    /// string, so without this the token would never appear for an existing install and every
    /// milestone plan would get checkbox instructions. Only a template that still matches
    /// <see cref="PilotSettings.LegacyPromptTemplateV1"/> exactly is replaced — a user who edited
    /// their prompt keeps it, token or no token.
    /// </remarks>
    PilotSettings Migrate(PilotSettings settings, int fileVersion)
    {
        if (fileVersion < 2 && IsLegacyDefaultPrompt(settings.PromptTemplate))
        {
            _logger.LogInformation(
                "The stored prompt template is the version 1 default; replacing it with the current one.");
            return settings with { PromptTemplate = PilotSettings.DefaultPromptTemplate };
        }

        return settings;
    }

    /// <summary>
    /// Whether a stored template is the untouched v1 default. Line endings are normalised first: the
    /// constant carries whatever this file was checked out with, and a settings file written on
    /// another machine may carry the other, which would otherwise make an untouched template look
    /// hand-written.
    /// </summary>
    static bool IsLegacyDefaultPrompt(string? template) =>
        string.Equals(
            NormalizeNewLines(template),
            NormalizeNewLines(PilotSettings.LegacyPromptTemplateV1),
            StringComparison.Ordinal);

    static string NormalizeNewLines(string? text) =>
        (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    PilotSettings Quarantine(string file, Exception? reason)
    {
        string? backup = null;
        try
        {
            backup = AtomicFile.BackUp(file, CorruptBackupSuffix);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not move the corrupt settings file aside.");
        }

        _logger.LogError(
            reason,
            "Settings file {Path} was unreadable; backed up to {Backup} and reset to defaults.",
            file,
            backup ?? "(no backup)");

        return new PilotSettings();
    }

    async Task WriteAsync(PilotSettings settings, CancellationToken cancellationToken)
    {
        _paths.EnsureCreated();
        var json = JsonSerializer.Serialize(settings, NightShiftJsonContext.Default.PilotSettings);
        await AtomicFile.WriteAllTextAsync(_paths.SettingsFile, json, cancellationToken).ConfigureAwait(false);
    }

    void Publish(PilotSettings settings)
    {
        Volatile.Write(ref _current, settings);
        SettingsChanged?.Invoke(this, settings);
    }
}
