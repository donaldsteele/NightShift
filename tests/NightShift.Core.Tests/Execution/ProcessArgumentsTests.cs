using System.Diagnostics;
using System.Runtime.InteropServices;
using NightShift.Core.Execution;

namespace NightShift.Core.Tests.Execution;

/// <summary>
/// The expected strings here are hand-derived from the <c>CommandLineToArgvW</c> rules and then
/// confirmed against the real parser by <see cref="Every_nasty_argument_survives_a_round_trip"/>,
/// which asks Windows itself to split the command line back up.
/// </summary>
public sealed class ProcessArgumentsTests
{
    /// <summary>Arguments chosen to hit every branch of the escaping rules at least once.</summary>
    public static readonly string[] NastyArguments =
    [
        "",
        " ",
        "plain",
        "two words",
        "\t",
        "trailing space ",
        @"C:\Program Files\nodejs\claude.cmd",
        @"C:\no-spaces\claude.exe",
        @"C:\Program Files\trailing\",
        @"C:\no-spaces\trailing\",
        @"\",
        @"\\",
        @"\\\\\",
        @"\\server\share\a b\",
        "\"",
        "\"\"",
        "say \"hi\"",
        "a\\\"b",
        "\\\\\"",
        "-p",
        "/caveman full\n\nContinue work on this project.",
        "--allowedTools",
        "Bash,Read,Edit,Write,Glob,Grep",
        "a\"b c\"d",
        "100% \"done\"",
    ];

    public static TheoryData<string> NastyArgumentData => [.. NastyArguments];

    [Theory]
    // Nothing that needs quoting is touched, so a logged command line stays readable.
    [InlineData("plain", "plain")]
    [InlineData("--verbose", "--verbose")]
    [InlineData(@"C:\no-spaces\claude.exe", @"C:\no-spaces\claude.exe")]
    // A trailing backslash is only special when a quote follows it.
    [InlineData(@"C:\no-spaces\trailing\", @"C:\no-spaces\trailing\")]
    [InlineData(@"\", @"\")]
    [InlineData(@"\\\", @"\\\")]
    // The empty argument cannot be expressed any other way.
    [InlineData("", "\"\"")]
    // Whitespace forces quoting; interior backslashes stay literal.
    [InlineData("two words", "\"two words\"")]
    [InlineData(@"C:\Program Files\nodejs\claude.cmd", "\"C:\\Program Files\\nodejs\\claude.cmd\"")]
    // ...but a run of backslashes immediately before the closing quote must be doubled.
    [InlineData(@"C:\Program Files\trailing\", "\"C:\\Program Files\\trailing\\\\\"")]
    [InlineData(@"a b\\", "\"a b\\\\\\\\\"")]
    // A quote is escaped with a backslash even when nothing else forces quoting.
    [InlineData("\"", "\"\\\"\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    // One backslash before a quote becomes three: two for the run, one to escape the quote.
    [InlineData("a\\\"b", "\"a\\\\\\\"b\"")]
    public void Quote_escapes_by_the_CommandLineToArgvW_rules(string argument, string expected) =>
        Assert.Equal(expected, ProcessArguments.Quote(argument));

    [Fact]
    public void Quote_rejects_a_null_argument() =>
        Assert.Throws<ArgumentNullException>(() => ProcessArguments.Quote(null!));

    [Fact]
    public void BuildCommandLine_joins_the_quoted_arguments_with_single_spaces()
    {
        var commandLine = ProcessArguments.BuildCommandLine(
        [
            "-p",
            "/caveman full",
            "--permission-mode",
            "auto",
        ]);

        Assert.Equal("-p \"/caveman full\" --permission-mode auto", commandLine);
    }

    [Fact]
    public void BuildCommandLine_of_nothing_is_empty() =>
        Assert.Equal(string.Empty, ProcessArguments.BuildCommandLine([]));

    [Fact]
    public void BuildCommandLine_rejects_a_null_element() =>
        Assert.Throws<ArgumentNullException>(() => ProcessArguments.BuildCommandLine(["ok", null!]));

    [Fact]
    public void The_command_shell_form_wraps_the_whole_command_in_an_extra_pair_of_quotes()
    {
        var arguments = ProcessArguments.BuildCommandShellArguments(
            @"C:\Users\me\AppData\Roaming\npm\claude.cmd",
            ["-p", "hello world"]);

        Assert.Equal(
            "/c \"\"C:\\Users\\me\\AppData\\Roaming\\npm\\claude.cmd\" -p \"hello world\"\"",
            arguments);
    }

    [Fact]
    public void The_command_shell_form_quotes_the_program_even_when_it_needs_no_quoting()
    {
        // Four quote characters put cmd.exe unambiguously in its "strip the outer pair" branch;
        // exactly two would risk the branch that preserves them.
        var arguments = ProcessArguments.BuildCommandShellArguments("claude.cmd", []);

        Assert.Equal("/c \"\"claude.cmd\"\"", arguments);
        Assert.Equal(4, arguments.Count(c => c == '"'));
    }

    [Fact]
    public void The_command_shell_form_rejects_a_missing_program()
    {
        Assert.Throws<ArgumentException>(() => ProcessArguments.BuildCommandShellArguments("  ", []));
        Assert.Throws<ArgumentNullException>(() => ProcessArguments.BuildCommandShellArguments(null!, []));
    }

    [Theory]
    [InlineData(@"C:\npm\claude.cmd", true)]
    [InlineData(@"C:\npm\claude.CMD", true)]
    [InlineData(@"C:\legacy\claude.bat", true)]
    [InlineData(@"C:\Programs\claude\claude.exe", false)]
    [InlineData("/home/me/.local/bin/claude", false)]
    [InlineData("claude", false)]
    public void RequiresCommandShell_is_true_only_for_batch_scripts(string program, bool expected) =>
        Assert.Equal(expected, ProcessArguments.RequiresCommandShell(program));

    [Theory]
    [InlineData("plain text", false)]
    [InlineData(@"C:\Program Files\a b\claude.cmd", false)]
    [InlineData("100% sure", true)]
    [InlineData("say \"hi\"", true)]
    [InlineData("bang!", true)]
    [InlineData("two\nlines", true)]
    public void HasCommandShellHazard_flags_what_cmd_would_reinterpret(string argument, bool expected) =>
        Assert.Equal(expected, ProcessArguments.HasCommandShellHazard(argument));

    [Theory]
    [MemberData(nameof(NastyArgumentData))]
    public void Every_nasty_argument_survives_a_round_trip(string argument)
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // CommandLineToArgvW is the reference implementation; there is no Unix analogue.
        }

        var parsed = SplitWithWindows(ProcessArguments.BuildCommandLine([argument]));

        Assert.Equal(new[] { argument }, parsed);
    }

    [Fact]
    public void A_whole_headless_argument_list_survives_a_round_trip()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The plan.md §5.4 argument list, with a prompt full of the characters that break naive
        // quoting.
        string[] arguments =
        [
            "-p",
            "/caveman full\n\nRead `plan.md`. Say \"done\" when the path C:\\work\\ is clean.",
            "--output-format",
            "stream-json",
            "--verbose",
            "--permission-mode",
            "auto",
            "--allowedTools",
            "Bash,Read,Edit,Write,Glob,Grep",
            "--disallowedTools",
            "AskUserQuestion",
        ];

        Assert.Equal(arguments, SplitWithWindows(ProcessArguments.BuildCommandLine(arguments)));
    }

    [Fact]
    public void All_the_nasty_arguments_survive_a_round_trip_together()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // An empty argument in the middle of a list is the case a "join with spaces" implementation
        // silently drops.
        Assert.Equal(NastyArguments, SplitWithWindows(ProcessArguments.BuildCommandLine(NastyArguments)));
    }

    [Fact]
    public void A_real_cmd_shim_in_a_path_with_spaces_receives_its_arguments_intact()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();

        // The npm shim case from plan.md §5.1: a .cmd under a directory with a space in its name.
        var directory = Path.Combine(temp.Path, "Program Files", "npm");
        Directory.CreateDirectory(directory);

        var shim = Path.Combine(directory, "echo-args.cmd");
        var output = Path.Combine(temp.Path, "args.txt");

        File.WriteAllText(shim, """
            @echo off
            :next
            if "%~1"=="" goto :eof
            >>"%NIGHTSHIFT_ARGS_OUT%" echo(%~1
            shift
            goto :next
            """);

        string[] arguments = ["-p", "continue work on this project", "--permission-mode", "auto"];

        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            Arguments = ProcessArguments.BuildCommandShellArguments(shim, arguments),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment["NIGHTSHIFT_ARGS_OUT"] = output;

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        Assert.True(process.WaitForExit(30_000), "the echo shim did not exit");
        Assert.Equal(0, process.ExitCode);

        var received = File.ReadAllLines(output).Select(line => line.TrimEnd()).ToArray();
        Assert.Equal(arguments, received);
    }

    /// <summary>
    /// Splits a command line the way every Windows process does. The first token of a real command
    /// line is parsed by different (program-name) rules, so a placeholder is prepended and dropped.
    /// </summary>
    static string[] SplitWithWindows(string argumentsCommandLine)
    {
        var handle = NativeMethods.CommandLineToArgvW("nightshift.exe " + argumentsCommandLine, out var count);
        Assert.NotEqual(IntPtr.Zero, handle);

        try
        {
            var parsed = new string[count - 1];
            for (var i = 1; i < count; i++)
            {
                parsed[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(handle, i * IntPtr.Size)) ?? string.Empty;
            }

            return parsed;
        }
        finally
        {
            NativeMethods.LocalFree(handle);
        }
    }

    static class NativeMethods
    {
#pragma warning disable SYSLIB1054 // A source-generated import would need AllowUnsafeBlocks in this test project.
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr LocalFree(IntPtr hMem);
#pragma warning restore SYSLIB1054
    }
}
