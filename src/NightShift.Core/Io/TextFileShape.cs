using System.Text;

namespace NightShift.Core.Io;

/// <summary>
/// The physical shape of a text file — its encoding, its line endings, and whether it ended with a
/// newline — captured on read so a save can put it all back.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of what a naive round-trip does to a git repository. Three separate things
/// go wrong without it, and all three produce the same symptom: a user edits two lines, saves, and
/// their next <c>git status</c> reports every line in the file as changed.
/// </para>
/// <list type="number">
/// <item><b>Line endings.</b> <c>.gitattributes</c> here is <c>* text=auto</c>, so on Windows the
/// working tree is CRLF — while a text box normalises everything the user types to LF.</item>
/// <item><b>The byte-order mark.</b> <c>File.ReadAllTextAsync</c> detects and strips one silently,
/// and <c>File.WriteAllTextAsync</c> with no encoding never writes one back.</item>
/// <item><b>The trailing newline.</b> POSIX text files end with one; editors disagree about
/// whether to keep it.</item>
/// </list>
/// <para>
/// None of this is visible on screen, which is exactly why it needs a test rather than an eye.
/// </para>
/// </remarks>
/// <param name="Encoding">What the file was read as, byte-order mark included if it had one.</param>
/// <param name="Newline">The dominant line ending — <c>"\r\n"</c> or <c>"\n"</c>.</param>
/// <param name="EndsWithNewline">Whether the file's last line was terminated.</param>
public sealed record TextFileShape(Encoding Encoding, string Newline, bool EndsWithNewline)
{
    /// <summary>UTF-8 without a mark, LF, trailing newline — what a new file should look like.</summary>
    public static TextFileShape Default { get; } =
        new(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), "\n", EndsWithNewline: true);

    /// <summary>
    /// Reads <paramref name="path"/> and reports both its text and its shape. The text is
    /// normalised to LF, which is what an editor wants; <see cref="Apply"/> puts the shape back.
    /// </summary>
    public static async Task<(string Text, TextFileShape Shape)> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // detectEncodingFromByteOrderMarks is the whole point: CurrentEncoding afterwards carries
        // the mark, so writing with it restores the file's original first three bytes.
        using var reader = new StreamReader(
            path,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: true);

        var raw = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        return (Normalize(raw), Detect(raw, reader.CurrentEncoding));
    }

    /// <summary>Writes <paramref name="text"/> back to <paramref name="path"/> in this shape.</summary>
    public Task WriteAsync(string path, string text, CancellationToken cancellationToken = default) =>
        AtomicFile.WriteAllTextAsync(path, Apply(text), Encoding, cancellationToken);

    /// <summary>Converts LF-normalised <paramref name="text"/> back into this file's conventions.</summary>
    public string Apply(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var body = Normalize(text);

        if (EndsWithNewline)
        {
            if (!body.EndsWith('\n'))
            {
                body += "\n";
            }
        }
        else
        {
            body = body.TrimEnd('\n');
        }

        return Newline == "\n" ? body : body.Replace("\n", Newline, StringComparison.Ordinal);
    }

    /// <summary>Every line ending becomes LF, whatever it was.</summary>
    static string Normalize(string text) =>
        text.Contains('\r', StringComparison.Ordinal)
            ? text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            : text;

    /// <summary>
    /// Picks the dominant newline. A mixed file is rewritten wholly in whichever style it had more
    /// of — which is a change, but a smaller and more explicable one than leaving it mixed.
    /// </summary>
    static TextFileShape Detect(string raw, Encoding encoding)
    {
        var crlf = 0;
        var lf = 0;

        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '\n')
            {
                continue;
            }

            if (i > 0 && raw[i - 1] == '\r')
            {
                crlf++;
            }
            else
            {
                lf++;
            }
        }

        // An empty or single-line file has no evidence either way, so it keeps the platform's.
        var newline = crlf == 0 && lf == 0
            ? Environment.NewLine
            : crlf >= lf ? "\r\n" : "\n";

        return new TextFileShape(encoding, newline, raw.EndsWith('\n'));
    }
}
