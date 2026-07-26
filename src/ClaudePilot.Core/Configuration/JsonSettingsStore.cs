using System.Text.Json;
using ClaudePilot.Core.Io;
using ClaudePilot.Core.Serialization;
using Microsoft.Extensions.Logging;

namespace ClaudePilot.Core.Configuration;

/// <summary>
/// Settings persisted as JSON at <c>%APPDATA%\ClaudePilot\settings.json</c>, written atomically.
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
            parsed = JsonSerializer.Deserialize(json, ClaudePilotJsonContext.Default.PilotSettings);
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
            normalized = normalized with { SettingsVersion = PilotSettings.CurrentVersion };
        }

        return (normalized, needsRewrite);
    }

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
        var json = JsonSerializer.Serialize(settings, ClaudePilotJsonContext.Default.PilotSettings);
        await AtomicFile.WriteAllTextAsync(_paths.SettingsFile, json, cancellationToken).ConfigureAwait(false);
    }

    void Publish(PilotSettings settings)
    {
        Volatile.Write(ref _current, settings);
        SettingsChanged?.Invoke(this, settings);
    }
}
