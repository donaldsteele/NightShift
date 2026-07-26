using System.Text;

namespace NightShift.Core.Execution;

/// <summary>
/// Quotes process arguments by the rules Windows actually implements, so nothing in this app ever
/// hand-concatenates a command line (plan.md §5.1 calls that out explicitly).
/// </summary>
/// <remarks>
/// <para>
/// Prefer <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> whenever the target is a
/// real executable — .NET applies exactly these rules for you and there is no string to get wrong.
/// This helper exists for the two cases where a raw command line is unavoidable: launching through
/// <c>cmd.exe /c</c> (the npm shim is a <c>.cmd</c>, plan.md §11.7) and logging the resolved command
/// line for dry-run mode (plan.md §9.2 "Advanced").
/// </para>
/// <para>
/// The rules encoded in <see cref="Quote(string)"/> are those of <c>CommandLineToArgvW</c>, which is
/// what the C runtime and .NET use to split a command line back into an argument vector. The subtle
/// part is backslashes: a run of backslashes is literal <em>unless</em> it is immediately followed by
/// a double quote, in which case every backslash in the run must be doubled — and a run at the very
/// end of a quoted argument counts as "followed by a quote" because of the closing quote we add.
/// </para>
/// </remarks>
public static class ProcessArguments
{
    /// <summary>The shell that must front a <c>.cmd</c>/<c>.bat</c> target on Windows.</summary>
    public const string CommandShellFileName = "cmd.exe";

    /// <summary>
    /// Characters that force an argument to be quoted. Space and tab are the separators
    /// <c>CommandLineToArgvW</c> splits on; the quote character has to be escaped wherever it
    /// appears; newline and vertical tab are quoted defensively because a bare one in a command line
    /// is at best confusing in a log and at worst truncating.
    /// </summary>
    static readonly char[] QuoteTriggers = [' ', '\t', '\n', '\v', '"'];

    /// <summary>Extensions that are batch scripts rather than images the loader can execute.</summary>
    static readonly string[] CommandShellExtensions = [".cmd", ".bat"];

    /// <summary>
    /// Characters <c>cmd.exe</c> re-interprets even inside double quotes, or that survive quoting
    /// only by accident. See <see cref="HasCommandShellHazard"/>.
    /// </summary>
    static readonly char[] CommandShellHazards = ['%', '!', '"', '\r', '\n'];

    /// <summary>
    /// Escapes one argument so <c>CommandLineToArgvW</c> hands the callee back the exact same string.
    /// An argument with no whitespace and no quote is returned untouched (that keeps command lines
    /// readable in logs); an empty argument becomes <c>""</c>, which is the only way to pass one.
    /// </summary>
    public static string Quote(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);

        var builder = new StringBuilder(argument.Length + 2);
        AppendArgument(builder, argument, force: false);
        return builder.ToString();
    }

    /// <summary>
    /// Joins an argument list into a single Win32 command line. The result round-trips: splitting it
    /// with <c>CommandLineToArgvW</c> yields the input sequence element for element.
    /// </summary>
    /// <remarks>
    /// This builds the <em>arguments</em> portion only. The program name is deliberately not part of
    /// it, because the first token of a real command line is parsed by different rules (no backslash
    /// escaping) and because <c>ProcessStartInfo</c> keeps the file name separate anyway.
    /// </remarks>
    public static string BuildCommandLine(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var builder = new StringBuilder();
        foreach (var argument in arguments)
        {
            ArgumentNullException.ThrowIfNull(argument, nameof(arguments));

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            AppendArgument(builder, argument, force: false);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Builds the value for <see cref="System.Diagnostics.ProcessStartInfo.Arguments"/> when running
    /// <paramref name="program"/> through <c>cmd.exe /c</c> — the form plan.md §5.1 prescribes for
    /// the npm <c>claude.cmd</c> shim: <c>cmd.exe /c ""&lt;path&gt;" &lt;args&gt;"</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The outer quotes are the whole point.</b> <c>cmd /?</c> documents two branches: with
    /// <em>exactly two</em> quote characters and an executable name between them, cmd preserves them;
    /// otherwise it strips the first character and the last quote and re-parses what is left. The
    /// second branch is the one we want, so the program is <em>always</em> quoted even when it needs
    /// no quoting — that guarantees at least four quote characters and puts us unambiguously in the
    /// "strip the outer pair" branch, whatever the arguments look like.
    /// </para>
    /// <para>
    /// <b>Use this only when you must.</b> <c>cmd.exe</c> parses the line before the callee's
    /// <c>CommandLineToArgvW</c> ever sees it, and the two disagree: <c>%VAR%</c> is expanded even
    /// inside quotes, and a <c>\"</c> escape means "literal quote" to the callee but "quoting is now
    /// off" to cmd. <see cref="HasCommandShellHazard"/> flags arguments where that matters. When the
    /// target is a real executable, set <c>ProcessStartInfo.FileName</c> to it and use
    /// <c>ArgumentList</c> instead — see <see cref="RequiresCommandShell"/>.
    /// </para>
    /// </remarks>
    /// <param name="program">Full path to the <c>.cmd</c>/<c>.bat</c> (or any program) to run.</param>
    /// <param name="arguments">Arguments for it, unquoted and unescaped.</param>
    public static string BuildCommandShellArguments(string program, IEnumerable<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        ArgumentNullException.ThrowIfNull(arguments);

        var inner = new StringBuilder();
        AppendArgument(inner, program, force: true);

        foreach (var argument in arguments)
        {
            ArgumentNullException.ThrowIfNull(argument, nameof(arguments));
            inner.Append(' ');
            AppendArgument(inner, argument, force: false);
        }

        return $"/c \"{inner}\"";
    }

    /// <summary>
    /// True when <paramref name="program"/> is a batch script, which the Win32 loader cannot execute
    /// directly — <c>CreateProcess</c> only ever appends <c>.exe</c>, so a <c>.cmd</c> has to go
    /// through <see cref="CommandShellFileName"/> (plan.md §11.7).
    /// </summary>
    public static bool RequiresCommandShell(string program)
    {
        ArgumentNullException.ThrowIfNull(program);

        var extension = Path.GetExtension(program);
        return CommandShellExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when routing <paramref name="argument"/> through <c>cmd.exe</c> would change it. Callers
    /// with such an argument — a prompt containing a quote, say — must launch the target directly
    /// rather than via <see cref="BuildCommandShellArguments"/>, or pass it by another channel.
    /// </summary>
    /// <remarks>
    /// <c>%</c> is environment expansion, <c>!</c> is delayed expansion (off by default, but a
    /// user's <c>cmd /v:on</c> autorun turns it on), <c>"</c> desynchronises cmd's quote tracking from
    /// the callee's, and a bare <c>\r</c> or <c>\n</c> terminates the line cmd is parsing.
    /// </remarks>
    public static bool HasCommandShellHazard(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);

        return argument.IndexOfAny(CommandShellHazards) >= 0;
    }

    /// <summary>
    /// The <c>CommandLineToArgvW</c> escaping rules. <paramref name="force"/> quotes an argument that
    /// would not otherwise need it.
    /// </summary>
    static void AppendArgument(StringBuilder builder, string argument, bool force)
    {
        if (!force && argument.Length > 0 && argument.IndexOfAny(QuoteTriggers) < 0)
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');

        var index = 0;
        while (index < argument.Length)
        {
            var c = argument[index++];

            if (c == '\\')
            {
                var backslashes = 1;
                while (index < argument.Length && argument[index] == '\\')
                {
                    index++;
                    backslashes++;
                }

                if (index == argument.Length)
                {
                    // The closing quote we are about to add would otherwise escape the last
                    // backslash, so the whole run has to be doubled. This is the case a naive
                    // "wrap it in quotes" implementation gets wrong for every Windows directory
                    // path the user pastes with a trailing separator.
                    builder.Append('\\', backslashes * 2);
                }
                else if (argument[index] == '"')
                {
                    // Double the run, then escape the quote itself with one more backslash.
                    builder.Append('\\', (backslashes * 2) + 1);
                    builder.Append('"');
                    index++;
                }
                else
                {
                    // Not before a quote: backslashes are literal, however many there are.
                    builder.Append('\\', backslashes);
                }
            }
            else if (c == '"')
            {
                builder.Append('\\').Append('"');
            }
            else
            {
                builder.Append(c);
            }
        }

        builder.Append('"');
    }
}
