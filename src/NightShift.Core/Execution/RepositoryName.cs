using System.Text;

namespace NightShift.Core.Execution;

/// <summary>
/// Works out what to call a project when a name is needed for something outside this machine —
/// today, the Remote Control session name.
/// </summary>
/// <remarks>
/// Reads <c>.git/config</c> directly rather than shelling out to <c>git</c>: no process to spawn,
/// nothing to time out, and it still works when git is not installed. Falls back to the directory
/// name, which is what a human would call the project anyway.
/// </remarks>
public static class RepositoryName
{
    /// <summary>Longest name produced. Long enough to be recognisable, short enough for a list.</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// The repository name for <paramref name="projectDirectory"/> — the last segment of its
    /// <c>origin</c> remote when there is one, otherwise the directory name — sanitised for use as
    /// a session name. Returns null when it cannot produce anything meaningful.
    /// </summary>
    public static string? Resolve(string? projectDirectory)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            return null;
        }

        var fromRemote = Sanitize(TryReadOriginName(projectDirectory));
        if (!string.IsNullOrEmpty(fromRemote))
        {
            return fromRemote;
        }

        var directory = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory)));
        var fromDirectory = Sanitize(directory);
        return string.IsNullOrEmpty(fromDirectory) ? null : fromDirectory;
    }

    /// <summary>
    /// The <c>origin</c> remote's repository name, or null. Handles both URL forms git uses:
    /// <c>https://host/owner/repo.git</c> and <c>git@host:owner/repo.git</c>.
    /// </summary>
    static string? TryReadOriginName(string projectDirectory)
    {
        string configPath;
        try
        {
            configPath = Path.Combine(projectDirectory, ".git", "config");
            if (!File.Exists(configPath))
            {
                // A worktree or submodule has a .git *file* pointing elsewhere. Not worth chasing —
                // the directory name is a perfectly good answer.
                return null;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(configPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var inOrigin = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();

            if (line.StartsWith('['))
            {
                inOrigin = line.Replace(" ", string.Empty, StringComparison.Ordinal)
                    .StartsWith("[remote\"origin\"]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inOrigin || !line.StartsWith("url", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0)
            {
                continue;
            }

            return LastSegment(line[(equals + 1)..].Trim());
        }

        return null;
    }

    static string? LastSegment(string url)
    {
        if (url.Length == 0)
        {
            return null;
        }

        var trimmed = url.TrimEnd('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        var cut = trimmed.LastIndexOfAny(['/', ':', '\\']);
        var segment = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
        return segment.Length == 0 ? null : segment;
    }

    /// <summary>
    /// Keeps letters, digits, dash, underscore and dot; everything else becomes a dash. Spaces in a
    /// directory name are common on Windows and would otherwise need quoting on a command line.
    /// </summary>
    static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        var lastWasDash = false;

        foreach (var ch in value.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')
            {
                builder.Append(ch);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                builder.Append('-');
                lastWasDash = true;
            }

            if (builder.Length >= MaxLength)
            {
                break;
            }
        }

        var result = builder.ToString().Trim('-', '.');
        return result.Length == 0 ? null : result;
    }
}
