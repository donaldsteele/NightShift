using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using NightShift.Core.Io;
using Microsoft.Extensions.Logging;

namespace NightShift.Core.Execution;

/// <summary>Whether a directory is already trusted, without touching the file (plan.md §5.3.1).</summary>
/// <param name="IsTrusted">True only when every expected key form carries all three trust flags.</param>
/// <param name="Keys">The key forms NightShift expects to find under <c>projects</c>.</param>
/// <param name="MissingKeys">The subset of <paramref name="Keys"/> not yet trusted.</param>
/// <param name="Problem">Why the answer could not be determined (absent or unparsable file).</param>
public sealed record WorkspaceTrustStatus(
    bool IsTrusted,
    IReadOnlyList<string> Keys,
    IReadOnlyList<string> MissingKeys,
    string? Problem);

/// <summary>What <see cref="WorkspaceTrustManager.ApplyAsync"/> did.</summary>
public enum WorkspaceTrustOutcome
{
    /// <summary>Every flag was already true; nothing was written.</summary>
    AlreadyTrusted,

    /// <summary>The file was rewritten (or created) with the trust keys.</summary>
    Applied,

    /// <summary>Nothing was written, because writing could have destroyed the user's config.</summary>
    Failed,
}

/// <param name="BackupPath">Path of the backup, whether created now or on an earlier run.</param>
public sealed record WorkspaceTrustApplyResult(
    WorkspaceTrustOutcome Outcome,
    IReadOnlyList<string> Keys,
    string? BackupPath,
    string? Error)
{
    /// <summary>True when the folder is trusted after this call, however it got that way.</summary>
    public bool IsTrusted => Outcome is WorkspaceTrustOutcome.AlreadyTrusted or WorkspaceTrustOutcome.Applied;
}

/// <summary>Result of the "restore my .claude.json backup" action (plan.md §11.4b).</summary>
public sealed record WorkspaceTrustRestoreResult(bool Restored, string BackupPath, string? Error);

/// <summary>
/// Writes the workspace-trust flags into the user's <b>global</b> <c>~/.claude.json</c> so a run
/// never stops at *"Do you trust the files in this folder?"* (plan.md §5.3.1).
/// </summary>
/// <remarks>
/// <para>
/// That one file holds the user's entire Claude Code configuration, so every rule in plan.md
/// §11.4b is load-bearing here:
/// </para>
/// <list type="bullet">
///   <item><description>
///     Parsed as a <see cref="JsonNode"/>, never into a typed model. Unknown top-level keys, nested
///     objects, arrays, and number precision all survive the round-trip untouched — the only nodes
///     this class ever assigns are the three booleans under <c>projects[&lt;dir&gt;]</c>.
///   </description></item>
///   <item><description>
///     Backed up once to <c>.claude.json.nightshift.bak</c> before the first write (the suffix
///     predates the rename from "pilot"; it is what the Advanced restore button looks for), then
///     written through <see cref="AtomicFile"/> so a crash mid-write cannot truncate it.
///   </description></item>
///   <item><description>
///     A file that exists but does not parse is <b>never</b> rewritten or quarantined —
///     <see cref="WorkspaceTrustOutcome.Failed"/> comes back instead. Garbage to us may be a file
///     a newer CLI understands perfectly well.
///   </description></item>
///   <item><description>
///     <b>Windows path normalization is the known failure mode.</b> Claude Code has written this
///     key with mixed separators, so both <c>C:\src\p</c> and <c>C:/src/p</c> are written; existing
///     keys are matched case-insensitively (and ignoring a trailing separator) so we update in
///     place rather than piling up near-duplicates.
///   </description></item>
/// </list>
/// <para>
/// <b>Concurrency.</b> Every public method is async and serialized against the others by an
/// internal gate, but that gate only covers this process. The Claude CLI rewrites
/// <c>~/.claude.json</c> wholesale when it exits, so the caller <b>must not</b> invoke
/// <see cref="ApplyAsync"/> or <see cref="RestoreBackupAsync"/> while a <c>claude</c> process is
/// running — take the same <c>RunGate</c> the runner takes (plan.md §5.3.1).
/// </para>
/// <para>
/// <b>Measured on Claude Code 2.1.220, 2026-07-26 — read this before trusting §5.3.1.</b>
/// A full headless run (<c>claude -p … --output-format stream-json --permission-mode auto</c>,
/// stdin closed) in a directory Claude Code had <em>never opened before</em> completed normally
/// with exit code 0, was never gated on the trust dialog, and added <b>no</b> entry to
/// <c>~/.claude.json</c> at all — the project count was identical before and after. So on this
/// version headless mode does not consult workspace trust, and applying trust must be treated as
/// <b>advisory</b>: attempt it when <c>AutoTrustProjectFolder</c> is set, report the outcome, and
/// never fail a run because it did not happen. This was <em>not</em> verified for the interactive
/// terminal path (plan.md §5.5), where the dialog demonstrably still exists, which is why this
/// class stays — that, and a future version may gate headless runs again.
/// </para>
/// <para>
/// The same session confirmed the mixed-separator bug is live: that machine's file held both
/// <c>"C:\\code\\TrestleBoard"</c> and <c>"C:/code/TrestleBoard"</c> for one folder, with six of
/// seven keys in forward-slash form. Writing both forms and matching existing keys
/// case- and trailing-separator-insensitively is therefore load-bearing, not paranoia.
/// </para>
/// </remarks>
public sealed class WorkspaceTrustManager
{
    /// <summary>Appended to the config path to form the backup name. Note: <b>nightshift</b>.</summary>
    public const string BackupSuffix = ".nightshift.bak";

    /// <summary>The global config file, always directly under the user profile.</summary>
    public const string ConfigFileName = ".claude.json";

    /// <summary>The object keyed by project directory.</summary>
    public const string ProjectsProperty = "projects";

    /// <summary>The three booleans that together suppress the trust dialog.</summary>
    public static readonly IReadOnlyList<string> TrustFlags =
    [
        "hasTrustDialogAccepted",
        "hasTrustDialogHooksAccepted",
        "hasCompletedProjectOnboarding",
    ];

    static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Indented, and with the relaxed encoder so non-ASCII paths and characters such as
    /// <c>&amp;</c> stay legible instead of being re-escaped — the file is read by humans and by a
    /// JavaScript CLI, never by a browser.
    /// </summary>
    static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    readonly ILogger<WorkspaceTrustManager> _logger;
    readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="configPath">
    /// Overrides the location of <c>.claude.json</c>. Exists so tests can point at a temp directory:
    /// nothing in the test suite may go anywhere near the real file.
    /// </param>
    public WorkspaceTrustManager(ILogger<WorkspaceTrustManager> logger, string? configPath = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ConfigPath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(configPath) ? DefaultConfigPath : configPath);
    }

    /// <summary><c>%USERPROFILE%\.claude.json</c>, or the platform equivalent.</summary>
    public static string DefaultConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ConfigFileName);

    /// <summary>The config file this instance reads and writes.</summary>
    public string ConfigPath { get; }

    /// <summary>Where the one-time backup lives.</summary>
    public string BackupPath => ConfigPath + BackupSuffix;

    /// <summary>True once a backup has been taken, which is what enables the restore action.</summary>
    public bool HasBackup => File.Exists(BackupPath);

    /// <summary>
    /// The key form(s) to write for <paramref name="projectDirectory"/>: the absolute backslash form
    /// and, when it differs, the same path with forward slashes. Trailing separators are stripped.
    /// </summary>
    public static IReadOnlyList<string> TrustKeysFor(string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);

        var native = TrimTrailingSeparators(Path.GetFullPath(projectDirectory));
        var forward = native.Replace('\\', '/');

        return string.Equals(native, forward, StringComparison.Ordinal)
            ? [native]
            : [native, forward];
    }

    /// <summary>
    /// Heuristic for plan.md §5.3.1's <c>TrustBlocked</c> condition: a run that produced no
    /// <c>system</c>/<c>init</c> event and whose output talks about trusting the folder.
    /// </summary>
    /// <remarks>
    /// Detection only. No retry loop is built on top of it, because on Claude Code 2.1.220 a
    /// headless run in a never-before-opened directory was never blocked on trust at all (see the
    /// measurement note on this class) — retrying an event we have no evidence occurs would be
    /// untestable machinery guarding nothing. If a future version starts blocking again, the
    /// runner has the predicate ready.
    /// </remarks>
    public static bool LooksTrustBlocked(bool sawInitEvent, string? output)
    {
        if (sawInitEvent || string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        foreach (var phrase in (string[])
                 ["do you trust", "trust the files", "trust dialog", "not trusted", "untrusted folder"])
        {
            if (output.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reports whether the folder is trusted <b>without writing anything</b>. Used by preflight to
    /// show the pill, and by the runner to re-verify before every run in case a CLI update reverted
    /// the keys (plan.md §11.4b).
    /// </summary>
    public async Task<WorkspaceTrustStatus> CheckAsync(
        string projectDirectory,
        CancellationToken cancellationToken = default)
    {
        var keys = TrustKeysFor(projectDirectory);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var read = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (read.Root is null)
            {
                return new WorkspaceTrustStatus(false, keys, keys, read.Problem);
            }

            var projects = read.Root[ProjectsProperty] as JsonObject;
            var missing = keys.Where(key => !IsTrusted(projects, key)).ToArray();

            return new WorkspaceTrustStatus(missing.Length == 0, keys, missing, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Sets the three trust flags for <paramref name="projectDirectory"/>, creating the file, the
    /// <c>projects</c> object, and the per-project object as needed. Backs up once and writes
    /// atomically. Returns <see cref="WorkspaceTrustOutcome.Failed"/> — having written nothing —
    /// when the existing file cannot be understood.
    /// </summary>
    /// <remarks>The caller must hold the run gate: see the concurrency note on the class.</remarks>
    public async Task<WorkspaceTrustApplyResult> ApplyAsync(
        string projectDirectory,
        CancellationToken cancellationToken = default)
    {
        var keys = TrustKeysFor(projectDirectory);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var read = await ReadAsync(cancellationToken).ConfigureAwait(false);

            // An existing file we cannot parse is left exactly as it is. Overwriting it would
            // destroy the user's whole configuration on the strength of our own parser.
            if (read.Root is null && read.Exists)
            {
                _logger.LogError("Not applying trust: {Path} could not be read ({Problem}).", ConfigPath, read.Problem);
                return Failed(keys, read.Problem);
            }

            var root = read.Root ?? [];
            var changed = false;

            if (root[ProjectsProperty] is not JsonObject projects)
            {
                if (root.TryGetPropertyValue(ProjectsProperty, out var existing) && existing is not null)
                {
                    return Failed(keys, $"`{ProjectsProperty}` in {ConfigPath} is not a JSON object.");
                }

                projects = [];
                root[ProjectsProperty] = projects;
                changed = true;
            }

            if (Validate(projects, keys) is { } invalid)
            {
                return Failed(keys, invalid);
            }

            foreach (var key in keys)
            {
                changed |= EnsureTrusted(projects, key);
            }

            if (!changed)
            {
                return new WorkspaceTrustApplyResult(
                    WorkspaceTrustOutcome.AlreadyTrusted, keys, HasBackup ? BackupPath : null, null);
            }

            var json = root.ToJsonString(WriteOptions);
            var backup = await WriteAsync(json, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Trusted {Keys} in {Path}.", string.Join(", ", keys), ConfigPath);

            return new WorkspaceTrustApplyResult(WorkspaceTrustOutcome.Applied, keys, backup, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not write trust keys to {Path}.", ConfigPath);
            return Failed(keys, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Puts the backup taken before our first write back in place (plan.md §11.4b, "restore my
    /// .claude.json backup"). The backup is kept, so the action can be repeated. A backup that is
    /// not valid JSON is refused rather than restored over a working file.
    /// </summary>
    public async Task<WorkspaceTrustRestoreResult> RestoreBackupAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(BackupPath))
            {
                return new WorkspaceTrustRestoreResult(false, BackupPath, "There is no backup to restore.");
            }

            string contents;
            try
            {
                contents = await File.ReadAllTextAsync(BackupPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Could not read the backup at {Path}.", BackupPath);
                return new WorkspaceTrustRestoreResult(false, BackupPath, ex.Message);
            }

            if (!IsParsableJson(contents))
            {
                return new WorkspaceTrustRestoreResult(
                    false, BackupPath, "The backup is not valid JSON; refusing to restore it.");
            }

            try
            {
                // Byte-for-byte what was there before our first write — no reserialization.
                await AtomicFile.WriteAllTextAsync(ConfigPath, contents, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Could not restore {Backup} to {Path}.", BackupPath, ConfigPath);
                return new WorkspaceTrustRestoreResult(false, BackupPath, ex.Message);
            }

            _logger.LogInformation("Restored {Path} from {Backup}.", ConfigPath, BackupPath);
            return new WorkspaceTrustRestoreResult(true, BackupPath, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Backs the config up once, then replaces it atomically. If the write fails after the backup
    /// was taken, the backup is moved straight back: we must never leave the user with no config.
    /// </summary>
    async Task<string?> WriteAsync(string json, CancellationToken cancellationToken)
    {
        var backup = HasBackup ? BackupPath : AtomicFile.BackUp(ConfigPath, BackupSuffix);

        try
        {
            await AtomicFile.WriteAllTextAsync(ConfigPath, json, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (backup is not null && !File.Exists(ConfigPath) && File.Exists(backup))
            {
                File.Move(backup, ConfigPath);
            }

            throw;
        }

        return backup;
    }

    async Task<(JsonObject? Root, bool Exists, string? Problem)> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ConfigPath))
        {
            return (null, false, $"{ConfigPath} does not exist yet.");
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(ConfigPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read {Path}.", ConfigPath);
            return (null, true, $"{ConfigPath} could not be read: {ex.Message}");
        }

        try
        {
            return JsonNode.Parse(json, nodeOptions: null, DocumentOptions) switch
            {
                JsonObject root => (root, true, null),
                null => (null, true, $"{ConfigPath} is empty."),
                _ => (null, true, $"{ConfigPath} is not a JSON object."),
            };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "{Path} is not valid JSON.", ConfigPath);
            return (null, true, $"{ConfigPath} is not valid JSON: {ex.Message}");
        }
    }

    /// <summary>
    /// Refuses up front if a key we are about to touch holds something other than an object, so the
    /// mutation below can never overwrite data it does not understand.
    /// </summary>
    static string? Validate(JsonObject projects, IReadOnlyList<string> keys)
    {
        foreach (var key in keys)
        {
            if (FindMatchingKey(projects, key) is { } name &&
                projects[name] is not null and not JsonObject)
            {
                return $"`{ProjectsProperty}[\"{name}\"]` is not a JSON object.";
            }
        }

        return null;
    }

    /// <summary>Sets the three flags on the matching entry, creating it if there isn't one.</summary>
    static bool EnsureTrusted(JsonObject projects, string key)
    {
        var name = FindMatchingKey(projects, key);

        if (name is null || projects[name] is not JsonObject project)
        {
            var created = new JsonObject();
            foreach (var flag in TrustFlags)
            {
                created[flag] = true;
            }

            // Assigning by the existing name keeps its position (and its capitalization) in the file.
            projects[name ?? key] = created;
            return true;
        }

        var changed = false;
        foreach (var flag in TrustFlags)
        {
            if (ReadBoolean(project[flag]) != true)
            {
                project[flag] = true;
                changed = true;
            }
        }

        return changed;
    }

    static bool IsTrusted(JsonObject? projects, string key)
    {
        if (projects is null || FindMatchingKey(projects, key) is not { } name)
        {
            return false;
        }

        return projects[name] is JsonObject project &&
               TrustFlags.All(flag => ReadBoolean(project[flag]) == true);
    }

    /// <summary>
    /// The existing property matching <paramref name="key"/> ignoring case and any trailing
    /// separator, or null. Separator <em>style</em> is deliberately significant: the whole point of
    /// §5.3.1 is that the CLI may look the folder up under either form, so both are maintained.
    /// </summary>
    static string? FindMatchingKey(JsonObject projects, string key)
    {
        foreach (var (name, _) in projects)
        {
            if (string.Equals(TrimTrailingSeparators(name), key, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>Strips trailing slashes, but never turns a drive root (<c>C:\</c>) into <c>C:</c>.</summary>
    static string TrimTrailingSeparators(string path)
    {
        var trimmed = path;

        while (trimmed.Length > 1 &&
               (trimmed[^1] == '\\' || trimmed[^1] == '/') &&
               trimmed[^2] != ':')
        {
            trimmed = trimmed[..^1];
        }

        return trimmed;
    }

    /// <summary>Older files have shipped these flags as the strings "true"/"false".</summary>
    static bool? ReadBoolean(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<bool>(out var flag))
        {
            return flag;
        }

        return value.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed)
            ? parsed
            : null;
    }

    static bool IsParsableJson(string contents)
    {
        try
        {
            return JsonNode.Parse(contents, nodeOptions: null, DocumentOptions) is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    static WorkspaceTrustApplyResult Failed(IReadOnlyList<string> keys, string? error) =>
        new(WorkspaceTrustOutcome.Failed, keys, null, error);
}
