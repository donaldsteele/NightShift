using System.Text;

namespace NightShift.Core.Io;

/// <summary>
/// Write-a-file-or-leave-the-old-one-intact helper. Used for every file NightShift owns and,
/// critically, for the user's global `~/.claude.json` later on (plan.md §5.3.1).
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// Writes <paramref name="contents"/> to a temp file in the *same* directory (so the move is a
    /// rename, not a cross-volume copy) and then replaces the destination in one operation.
    /// A crash mid-write leaves the previous file untouched.
    /// </summary>
    /// <summary>
    /// UTF-8 with no byte-order mark — byte-for-byte what <c>File.WriteAllTextAsync</c> writes when
    /// given no encoding, which is what every caller here got before the overload below existed.
    /// </summary>
    static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static Task WriteAllTextAsync(
        string path,
        string contents,
        CancellationToken cancellationToken = default) =>
        WriteAllTextAsync(path, contents, Utf8NoBom, cancellationToken);

    /// <inheritdoc cref="WriteAllTextAsync(string, string, CancellationToken)"/>
    /// <param name="encoding">
    /// What to write with, byte-order mark included if it has one. This overload exists because the
    /// plan editor has to write a file back the way it found it: a plan saved with a BOM must keep
    /// it, and silently dropping one turns a two-line edit into a whole-file diff in the user's
    /// next commit.
    /// </param>
    public static async Task WriteAllTextAsync(
        string path,
        string contents,
        Encoding encoding,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(encoding);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException($"Path has no directory component: {path}", nameof(path));

        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():n}.tmp");

        try
        {
            await File.WriteAllTextAsync(tempPath, contents, encoding, cancellationToken)
                .ConfigureAwait(false);
            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Renames <paramref name="path"/> out of the way, appending <paramref name="suffix"/> and, if
    /// that name is taken, a numeric discriminator. Returns the backup path, or null if there was
    /// nothing to back up.
    /// </summary>
    public static string? BackUp(string path, string suffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(suffix);

        if (!File.Exists(path))
        {
            return null;
        }

        var candidate = path + suffix;
        for (var i = 1; File.Exists(candidate); i++)
        {
            candidate = $"{path}{suffix}-{i}";
        }

        File.Move(path, candidate);
        return candidate;
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp file is harmless.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
