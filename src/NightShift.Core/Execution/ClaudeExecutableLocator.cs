using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.Extensions.Logging;
using NightShift.Core.Configuration;

namespace NightShift.Core.Execution;

/// <summary>Where a resolved Claude Code executable came from (plan.md §5.1's search order).</summary>
public enum ClaudeExecutableSource
{
    /// <summary>Nothing was found; see <see cref="ClaudeExecutableResolution.FailureReason"/>.</summary>
    NotFound,

    /// <summary>The explicit <see cref="PilotSettings.ClaudeExecutablePath"/> override.</summary>
    ConfiguredPath,

    /// <summary>A directory on <c>PATH</c>.</summary>
    SearchPath,

    /// <summary>One of the documented install locations (npm shim, installer, <c>~/.local/bin</c>).</summary>
    WellKnownLocation,
}

/// <summary>
/// The outcome of a lookup: what was found, how it was found, and — when nothing was — a reason the
/// preflight strip (plan.md §7) can show the user verbatim.
/// </summary>
/// <param name="ExecutablePath">Absolute path to the CLI, or null when the lookup failed.</param>
/// <param name="Source">Which rule in plan.md §5.1 produced the hit.</param>
/// <param name="FailureReason">Human-readable explanation, null on success.</param>
/// <param name="ProbedPaths">
/// Every path that was tested, in order. Kept because "Claude Code not found on PATH" is useless on
/// its own when the real problem is that the user's PATH does not contain the npm prefix.
/// </param>
/// <remarks>
/// The <see cref="ProbedPaths"/> list makes this record's generated equality reference-based for that
/// member. That is fine — nothing compares resolutions — but do not start using it as a dictionary
/// key expecting value semantics.
/// </remarks>
public sealed record ClaudeExecutableResolution(
    string? ExecutablePath,
    ClaudeExecutableSource Source,
    string? FailureReason,
    IReadOnlyList<string> ProbedPaths)
{
    /// <summary>True when <see cref="ExecutablePath"/> points at a file that existed at lookup time.</summary>
    [MemberNotNullWhen(true, nameof(ExecutablePath))]
    public bool IsFound => ExecutablePath is not null;

    /// <summary>
    /// True when the hit is a <c>.cmd</c>/<c>.bat</c> shim and therefore has to be launched through
    /// <c>cmd.exe</c> rather than started directly (plan.md §5.1, §11.7).
    /// </summary>
    public bool RequiresCommandShell => IsFound && ProcessArguments.RequiresCommandShell(ExecutablePath);

    internal static ClaudeExecutableResolution Found(
        string path,
        ClaudeExecutableSource source,
        IReadOnlyList<string> probedPaths) => new(path, source, null, probedPaths);

    internal static ClaudeExecutableResolution NotFound(
        string reason,
        IReadOnlyList<string> probedPaths) => new(null, ClaudeExecutableSource.NotFound, reason, probedPaths);
}

/// <summary>
/// The ambient state <see cref="ClaudeExecutableLocator"/> reads. Behind an interface so the search
/// can be unit tested against a made-up machine — a test that consulted the real <c>PATH</c> would
/// pass or fail depending on whether the developer happens to have Claude Code installed.
/// </summary>
public interface IExecutableEnvironment
{
    /// <summary>Drives PATHEXT probing and which install locations are worth testing.</summary>
    bool IsWindows { get; }

    /// <summary>Process environment variable, or null when unset.</summary>
    string? GetEnvironmentVariable(string name);

    /// <summary>Special folder path, or empty string when the platform has no such folder.</summary>
    string GetFolderPath(Environment.SpecialFolder folder);

    /// <summary>Whether a file exists at <paramref name="path"/>.</summary>
    bool FileExists(string path);
}

/// <summary>The real machine.</summary>
public sealed class SystemExecutableEnvironment : IExecutableEnvironment
{
    /// <summary>Shared instance; the type is stateless.</summary>
    public static SystemExecutableEnvironment Instance { get; } = new();

    public bool IsWindows => OperatingSystem.IsWindows();

    public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);

    public string GetFolderPath(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);

    public bool FileExists(string path) => File.Exists(path);
}

/// <summary>Finds the Claude Code CLI (plan.md §5.1).</summary>
public interface IClaudeExecutableLocator
{
    /// <summary>
    /// Resolves the CLI for these settings. Never throws and never caches: the file can appear or
    /// vanish between runs (an npm update, a user editing the path), and preflight re-checks before
    /// every scheduled run anyway.
    /// </summary>
    ClaudeExecutableResolution Locate(PilotSettings settings);
}

/// <summary>
/// Resolves the Claude Code executable in the order plan.md §5.1 lays down:
/// <list type="number">
/// <item>the configured <see cref="PilotSettings.ClaudeExecutablePath"/>, if that file exists;</item>
/// <item><c>claude.cmd</c> / <c>claude.exe</c> / <c>claude</c> on <c>PATH</c>, probing <c>PATHEXT</c>
/// on Windows;</item>
/// <item><c>%APPDATA%\npm\claude.cmd</c>, <c>%LOCALAPPDATA%\Programs\claude\claude.exe</c>,
/// <c>~/.local/bin/claude</c>.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// The <c>.cmd</c> shim is tried before <c>.exe</c> deliberately: a global npm install
/// (<c>npm i -g @anthropic-ai/claude-code</c>) is the common way in, and it drops a <c>.cmd</c>
/// alongside an extensionless shell script in the same directory. Picking the <c>.cmd</c> is what
/// makes <see cref="ClaudeExecutableResolution.RequiresCommandShell"/> true and sends the caller down
/// the <c>cmd.exe /c</c> path in <see cref="ProcessArguments"/>.
/// </para>
/// <para>
/// A configured path that no longer exists does not abort the search — the user may have moved the
/// install and never updated Settings, and finding a working CLI beats failing the run. It is logged
/// and, if nothing else is found either, named in the failure reason.
/// </para>
/// </remarks>
public sealed class ClaudeExecutableLocator : IClaudeExecutableLocator
{
    /// <summary>Base name of the CLI, without any extension.</summary>
    public const string ProgramName = "claude";

    /// <summary>What Windows uses when <c>PATHEXT</c> is unset. Matches the documented default.</summary>
    public const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD";

    readonly IExecutableEnvironment _environment;
    readonly ILogger<ClaudeExecutableLocator> _logger;

    public ClaudeExecutableLocator(
        ILogger<ClaudeExecutableLocator> logger,
        IExecutableEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _environment = environment ?? SystemExecutableEnvironment.Instance;
    }

    public ClaudeExecutableResolution Locate(PilotSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return Locate(settings.ClaudeExecutablePath);
    }

    /// <summary>
    /// Same search with the override supplied directly, for callers that hold a candidate path
    /// rather than a settings record — the Settings screen validating a file the user just picked,
    /// for instance.
    /// </summary>
    public ClaudeExecutableResolution Locate(string? configuredPath)
    {
        var probed = new List<string>();

        var configured = Normalize(configuredPath);
        if (configured is not null)
        {
            probed.Add(configured);
            if (_environment.FileExists(configured))
            {
                return ClaudeExecutableResolution.Found(
                    configured,
                    ClaudeExecutableSource.ConfiguredPath,
                    probed);
            }

            _logger.LogWarning(
                "The configured Claude executable {Path} does not exist; falling back to PATH.",
                configured);
        }

        foreach (var candidate in SearchPathCandidates())
        {
            probed.Add(candidate);
            if (_environment.FileExists(candidate))
            {
                _logger.LogDebug("Found Claude Code on PATH at {Path}.", candidate);
                return ClaudeExecutableResolution.Found(
                    candidate,
                    ClaudeExecutableSource.SearchPath,
                    probed);
            }
        }

        foreach (var candidate in WellKnownCandidates())
        {
            probed.Add(candidate);
            if (_environment.FileExists(candidate))
            {
                _logger.LogDebug("Found Claude Code in a known install location at {Path}.", candidate);
                return ClaudeExecutableResolution.Found(
                    candidate,
                    ClaudeExecutableSource.WellKnownLocation,
                    probed);
            }
        }

        var reason = BuildFailureReason(configured, probed.Count);
        _logger.LogWarning("{Reason}", reason);
        return ClaudeExecutableResolution.NotFound(reason, probed);
    }

    /// <summary>
    /// Every <c>&lt;PATH directory&gt;\&lt;candidate name&gt;</c> combination, directories in PATH
    /// order and names in preference order within each directory — the same precedence the shell
    /// itself applies, so we never resolve to a different <c>claude</c> than the user's terminal does.
    /// </summary>
    IEnumerable<string> SearchPathCandidates()
    {
        var names = CandidateFileNames();

        foreach (var directory in SearchDirectories())
        {
            foreach (var name in names)
            {
                var combined = TryCombine(directory, name);
                if (combined is not null)
                {
                    yield return combined;
                }
            }
        }
    }

    /// <summary>
    /// File names to try in one directory. On Windows the two documented shim names come first, then
    /// anything else <c>PATHEXT</c> would make executable, then the extensionless script (which a
    /// Git-Bash-style shell would pick up but <c>CreateProcess</c> cannot start — the caller decides
    /// what to do with it). Elsewhere there is only ever the bare name.
    /// </summary>
    IReadOnlyList<string> CandidateFileNames()
    {
        if (!_environment.IsWindows)
        {
            return [ProgramName];
        }

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in (string[])[ProgramName + ".cmd", ProgramName + ".exe"])
        {
            if (seen.Add(name))
            {
                names.Add(name);
            }
        }

        foreach (var extension in PathExtensions())
        {
            var name = ProgramName + extension;
            if (seen.Add(name))
            {
                names.Add(name);
            }
        }

        if (seen.Add(ProgramName))
        {
            names.Add(ProgramName);
        }

        return names;
    }

    IEnumerable<string> PathExtensions()
    {
        var pathExt = _environment.GetEnvironmentVariable("PATHEXT");
        if (string.IsNullOrWhiteSpace(pathExt))
        {
            pathExt = DefaultPathExt;
        }

        foreach (var entry in pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // PATHEXT entries are supposed to include the dot; tolerate the ones that do not rather
            // than silently skipping a user's hand-edited value. It is also conventionally uppercase
            // (".COM;.EXE;…"), which the case-insensitive filesystem does not care about but a path
            // shown in the UI or a log does — so the candidate is spelled the way the file is.
            var extension = entry.ToLowerInvariant();
            yield return extension.StartsWith('.') ? extension : "." + extension;
        }
    }

    IEnumerable<string> SearchDirectories()
    {
        var path = _environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // A PATH entry may be quoted on Windows; the quotes are not part of the directory name.
            var directory = entry.Trim('"');
            if (directory.Length > 0 && seen.Add(directory))
            {
                yield return directory;
            }
        }
    }

    /// <summary>
    /// The install locations from plan.md §5.1. All three are probed on every platform: a special
    /// folder that does not apply resolves to something no installer writes to, and the existence
    /// check costs a stat — cheaper than a platform matrix that has to be kept in step with the
    /// installers.
    /// </summary>
    IEnumerable<string> WellKnownCandidates()
    {
        var candidates = new (Environment.SpecialFolder Folder, string[] Segments)[]
        {
            (Environment.SpecialFolder.ApplicationData, ["npm", ProgramName + ".cmd"]),
            (Environment.SpecialFolder.LocalApplicationData, ["Programs", ProgramName, ProgramName + ".exe"]),
            (Environment.SpecialFolder.UserProfile, [".local", "bin", ProgramName]),
        };

        foreach (var (folder, segments) in candidates)
        {
            var root = _environment.GetFolderPath(folder);
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var combined = TryCombine(root, segments);
            if (combined is not null)
            {
                yield return combined;
            }
        }
    }

    string BuildFailureReason(string? configuredMiss, int probedCount)
    {
        var message = new StringBuilder("Claude Code was not found. ");

        if (configuredMiss is not null)
        {
            message
                .Append("The configured executable path '")
                .Append(configuredMiss)
                .Append("' does not exist. ");
        }

        message
            .Append("Tried ")
            .Append(probedCount)
            .Append(probedCount == 1 ? " location: " : " locations: ")
            .Append(string.Join(", ", CandidateFileNames()))
            .Append(" on PATH, then the usual install locations (npm prefix, ")
            .Append(_environment.IsWindows ? @"%LOCALAPPDATA%\Programs\claude" : "~/.local/bin")
            .Append("). Install Claude Code or set the executable path in Settings.");

        return message.ToString();
    }

    /// <summary>
    /// Trims and absolutises a user-supplied path. Returns null for "not set" and for anything the
    /// filesystem would reject — a settings file can contain arbitrary text and must not be able to
    /// throw out of a lookup.
    /// </summary>
    static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim().Trim('"');
        if (trimmed.Length == 0)
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException
                                       or IOException or System.Security.SecurityException)
        {
            return null;
        }
    }

    static string? TryCombine(string directory, params string[] segments)
    {
        try
        {
            return Path.Combine([directory, .. segments]);
        }
        catch (ArgumentException)
        {
            // An entry with invalid path characters. Skip it; PATH is user-editable and often junk.
            return null;
        }
    }
}
