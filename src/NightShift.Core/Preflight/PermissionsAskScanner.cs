using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NightShift.Core.Preflight;

/// <summary>Which settings file a finding came from (plan.md §5.3.2).</summary>
public enum SettingsScope
{
    /// <summary><c>~/.claude/settings.json</c>.</summary>
    User,

    /// <summary><c>&lt;project&gt;/.claude/settings.json</c>, usually committed.</summary>
    Project,

    /// <summary><c>&lt;project&gt;/.claude/settings.local.json</c>, usually gitignored.</summary>
    ProjectLocal,
}

/// <summary>What kind of thing was found.</summary>
public enum PermissionsFindingKind
{
    /// <summary>A <c>permissions.ask</c> entry: evaluated before the classifier, always prompts.</summary>
    AskRule,

    /// <summary>An MCP server flagged <c>requiresUserInteraction</c>.</summary>
    InteractiveMcpServer,

    /// <summary>The file exists but could not be read or parsed, so it could not be cleared.</summary>
    Unreadable,
}

/// <param name="Rule">The rule text (e.g. <c>Bash(rm:*)</c>), the server name, or the parse error.</param>
public sealed record PermissionsFinding(
    PermissionsFindingKind Kind,
    SettingsScope Scope,
    string FilePath,
    string Rule)
{
    /// <summary>One line for the preflight list.</summary>
    public override string ToString() => Kind switch
    {
        PermissionsFindingKind.AskRule => $"ask rule `{Rule}` in {FilePath}",
        PermissionsFindingKind.InteractiveMcpServer => $"MCP server `{Rule}` requires interaction ({FilePath})",
        _ => $"{FilePath} could not be read: {Rule}",
    };
}

/// <param name="Exists">False for a file that simply is not there — normal, and not a finding.</param>
/// <param name="Error">Non-null when the file exists but could not be read or parsed.</param>
public sealed record ScannedSettingsFile(SettingsScope Scope, string Path, bool Exists, string? Error);

/// <summary>Everything the scan saw, so the UI can list both the blockers and where it looked.</summary>
public sealed record PermissionsScanResult(
    IReadOnlyList<PermissionsFinding> Findings,
    IReadOnlyList<ScannedSettingsFile> Files)
{
    public static PermissionsScanResult Empty { get; } = new([], []);

    public IEnumerable<PermissionsFinding> AskRules =>
        Findings.Where(f => f.Kind == PermissionsFindingKind.AskRule);

    public IEnumerable<PermissionsFinding> InteractiveMcpServers =>
        Findings.Where(f => f.Kind == PermissionsFindingKind.InteractiveMcpServer);

    /// <summary>True when something found here can stop an unattended run dead.</summary>
    public bool HasBlockers =>
        Findings.Any(f => f.Kind != PermissionsFindingKind.Unreadable);

    /// <summary>True when a file could not be read, so the answer above is not conclusive.</summary>
    public bool HasUnreadableFiles =>
        Findings.Any(f => f.Kind == PermissionsFindingKind.Unreadable);
}

/// <summary>
/// Finds the settings that will hang an unattended run: <c>permissions.ask</c> rules and MCP servers
/// marked <c>requiresUserInteraction</c> (plan.md §5.3.2).
/// </summary>
/// <remarks>
/// <para>
/// Ask rules are the nastier of the two, because they look harmless. They are evaluated
/// <b>before</b> the auto-mode classifier and always force a prompt, so a single
/// <c>"Bash(git push:*)"</c> in the user's own <c>~/.claude/settings.json</c> silently converts
/// every unattended run into one that sits waiting for a keypress until the stall detector kills
/// it. Preflight lists them by file and rule so the user can go and delete the right line.
/// </para>
/// <para>
/// Nothing here throws. A missing file is normal and is recorded, not reported as a problem; a file
/// that exists but cannot be read or parsed becomes an <see cref="PermissionsFindingKind.Unreadable"/>
/// finding, because "we could not check" is a different answer from "there is nothing there" and the
/// user deserves to be told which one they got. Nothing here writes, either — this class only reads.
/// </para>
/// </remarks>
public sealed class PermissionsAskScanner
{
    public const string SettingsFileName = "settings.json";
    public const string LocalSettingsFileName = "settings.local.json";
    public const string ProjectSettingsDirectory = ".claude";

    /// <summary>Depth limit for the <c>mcpServers</c> walk; the flag can sit on a nested tool entry.</summary>
    const int MaxMcpSearchDepth = 4;

    static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    readonly ILogger<PermissionsAskScanner> _logger;

    /// <param name="userSettingsPath">
    /// Overrides <c>~/.claude/settings.json</c>. Injectable so tests read a temp directory and never
    /// the developer's own configuration.
    /// </param>
    public PermissionsAskScanner(ILogger<PermissionsAskScanner> logger, string? userSettingsPath = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        UserSettingsPath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(userSettingsPath) ? DefaultUserSettingsPath : userSettingsPath);
    }

    /// <summary><c>~/.claude/settings.json</c>.</summary>
    public static string DefaultUserSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ProjectSettingsDirectory,
        SettingsFileName);

    /// <summary>The user-scope file this instance reads.</summary>
    public string UserSettingsPath { get; }

    /// <summary>
    /// Scans the user file plus the project's <c>.claude/settings.json</c> and
    /// <c>.claude/settings.local.json</c>. An empty <paramref name="projectDirectory"/> scans the
    /// user file only, which is what preflight wants before a project has been picked.
    /// </summary>
    public async Task<PermissionsScanResult> ScanAsync(
        string? projectDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var findings = new List<PermissionsFinding>();
        var files = new List<ScannedSettingsFile>();

        foreach (var (scope, path) in ResolveFiles(projectDirectory))
        {
            await ScanFileAsync(scope, path, findings, files, cancellationToken).ConfigureAwait(false);
        }

        if (findings.Count > 0)
        {
            _logger.LogWarning(
                "Settings that can pause an unattended run: {Findings}",
                string.Join("; ", findings.Select(f => f.ToString())));
        }

        return new PermissionsScanResult(findings, files);
    }

    /// <summary>The exact files this scanner reads, in precedence order (user first).</summary>
    public IReadOnlyList<(SettingsScope Scope, string Path)> ResolveFiles(string? projectDirectory)
    {
        var files = new List<(SettingsScope, string)> { (SettingsScope.User, UserSettingsPath) };

        if (!string.IsNullOrWhiteSpace(projectDirectory))
        {
            var claudeDirectory = Path.Combine(Path.GetFullPath(projectDirectory), ProjectSettingsDirectory);
            files.Add((SettingsScope.Project, Path.Combine(claudeDirectory, SettingsFileName)));
            files.Add((SettingsScope.ProjectLocal, Path.Combine(claudeDirectory, LocalSettingsFileName)));
        }

        return files;
    }

    async Task ScanFileAsync(
        SettingsScope scope,
        string path,
        List<PermissionsFinding> findings,
        List<ScannedSettingsFile> files,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            files.Add(new ScannedSettingsFile(scope, path, Exists: false, Error: null));
            return;
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Unreadable(scope, path, ex.Message, findings, files);
            return;
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json, nodeOptions: null, DocumentOptions) as JsonObject;
        }
        catch (JsonException ex)
        {
            Unreadable(scope, path, ex.Message, findings, files);
            return;
        }

        if (root is null)
        {
            Unreadable(scope, path, "the file is not a JSON object.", findings, files);
            return;
        }

        files.Add(new ScannedSettingsFile(scope, path, Exists: true, Error: null));

        CollectAskRules(scope, path, root, findings);
        CollectInteractiveMcpServers(scope, path, root, findings);
    }

    static void CollectAskRules(
        SettingsScope scope,
        string path,
        JsonObject root,
        List<PermissionsFinding> findings)
    {
        if (root["permissions"] is not JsonObject permissions ||
            !permissions.TryGetPropertyValue("ask", out var ask) ||
            ask is null)
        {
            return;
        }

        if (ask is not JsonArray rules)
        {
            // A malformed `ask` is still worth surfacing: whatever it means to the CLI, we cannot
            // promise it will not prompt.
            findings.Add(new PermissionsFinding(
                PermissionsFindingKind.Unreadable,
                scope,
                path,
                "`permissions.ask` is not an array."));
            return;
        }

        foreach (var rule in rules)
        {
            var text = rule switch
            {
                null => "(null)",
                JsonValue value when value.TryGetValue<string>(out var s) => s,
                _ => rule.ToJsonString(),
            };

            findings.Add(new PermissionsFinding(PermissionsFindingKind.AskRule, scope, path, text));
        }
    }

    /// <summary>
    /// Reports each MCP server whose definition carries <c>requiresUserInteraction: true</c>, at the
    /// server level or on a nested tool entry.
    /// </summary>
    static void CollectInteractiveMcpServers(
        SettingsScope scope,
        string path,
        JsonObject root,
        List<PermissionsFinding> findings)
    {
        if (root["mcpServers"] is not JsonObject servers)
        {
            return;
        }

        foreach (var (name, definition) in servers)
        {
            if (RequiresUserInteraction(definition, depth: 0))
            {
                findings.Add(new PermissionsFinding(
                    PermissionsFindingKind.InteractiveMcpServer, scope, path, name));
            }
        }
    }

    static bool RequiresUserInteraction(JsonNode? node, int depth)
    {
        if (node is null || depth > MaxMcpSearchDepth)
        {
            return false;
        }

        switch (node)
        {
            case JsonObject obj:
                if (obj.TryGetPropertyValue("requiresUserInteraction", out var flag) &&
                    ReadBoolean(flag) == true)
                {
                    return true;
                }

                return obj.Any(pair => RequiresUserInteraction(pair.Value, depth + 1));

            case JsonArray array:
                return array.Any(element => RequiresUserInteraction(element, depth + 1));

            default:
                return false;
        }
    }

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

    void Unreadable(
        SettingsScope scope,
        string path,
        string error,
        List<PermissionsFinding> findings,
        List<ScannedSettingsFile> files)
    {
        _logger.LogWarning("Could not check {Path} for ask rules: {Error}", path, error);
        files.Add(new ScannedSettingsFile(scope, path, Exists: true, error));
        findings.Add(new PermissionsFinding(PermissionsFindingKind.Unreadable, scope, path, error));
    }
}
