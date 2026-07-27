using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NightShift.Core.Execution;

namespace NightShift.Core.Tests.Execution;

public sealed class ClaudeTerminalLauncherTests
{
    readonly List<ProcessStartInfo> _started = [];

    Func<ProcessStartInfo, bool> _startBehaviour = _ => true;

    ClaudeTerminalLauncher Create() => new(
        NullLogger.Instance,
        info =>
        {
            _started.Add(info);
            return _startBehaviour(info);
        });

    static string CommandOf(ProcessStartInfo info) =>
        info.FileName == "wt.exe" ? string.Join(' ', info.ArgumentList) : info.Arguments;

    [Fact]
    public void Windows_Terminal_is_preferred_and_runs_in_the_working_directory()
    {
        var ok = Create().TryLaunch(@"C:\claude.cmd", @"C:\project", "plan", null, null, out var error);

        Assert.True(ok);
        Assert.Null(error);
        var start = Assert.Single(_started);
        Assert.Equal("wt.exe", start.FileName);
        Assert.Equal(@"C:\project", start.WorkingDirectory);
        Assert.Contains("--permission-mode plan", CommandOf(start), StringComparison.Ordinal);
    }

    [Fact]
    public void It_falls_back_to_cmd_when_Windows_Terminal_is_missing()
    {
        _startBehaviour = info => info.FileName != "wt.exe";

        var ok = Create().TryLaunch(@"C:\claude.cmd", @"C:\project", "plan", null, null, out _);

        Assert.True(ok);
        Assert.Equal(2, _started.Count);
        Assert.Equal("cmd.exe", _started[1].FileName);
        Assert.Equal(@"C:\project", _started[1].WorkingDirectory);
        Assert.Contains("--permission-mode plan", _started[1].Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void When_nothing_can_be_started_it_reports_rather_than_throws()
    {
        _startBehaviour = _ => false;

        var ok = Create().TryLaunch(@"C:\claude.cmd", @"C:\project", "plan", null, null, out var error);

        Assert.False(ok);
        Assert.Equal("Neither Windows Terminal nor cmd.exe could be started.", error);
    }

    [Fact]
    public void The_mode_is_passed_through_verbatim()
    {
        // A string, not the PermissionMode enum: the enum has no Plan value on purpose, and
        // widening it would put plan mode into the unattended scheduler.
        Create().TryLaunch(@"C:\claude.cmd", @"C:\project", "acceptEdits", null, null, out _);

        Assert.Contains("--permission-mode acceptEdits", CommandOf(_started[0]), StringComparison.Ordinal);
    }

    [Fact]
    public void Extra_arguments_are_appended_after_the_mode()
    {
        Create().TryLaunch(
            @"C:\claude.cmd", @"C:\project", "auto", null, "--remote-control \"My Repo\"", out _);

        Assert.Contains(
            "--permission-mode auto --remote-control \"My Repo\"",
            CommandOf(_started[0]),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_seed_prompt_is_passed_as_the_positional_argument()
    {
        const string Prompt = "Read plan.md in this project and help me edit it.";

        Create().TryLaunch(@"C:\claude.cmd", @"C:\project", "plan", Prompt, null, out _);

        Assert.Contains($"\"{Prompt}\"", CommandOf(_started[0]), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("has a % in it")]
    [InlineData("has a ! in it")]
    [InlineData("has a \" in it")]
    public void A_prompt_cmd_would_reinterpret_is_dropped_and_the_session_still_opens(string prompt)
    {
        // cmd.exe parses this line before the callee does, and the two disagree about %, ! and ".
        // Mangling the prompt would be worse than not sending it -- the caller has already put the
        // full text on the clipboard.
        var ok = Create().TryLaunch(@"C:\claude.cmd", @"C:\project", "plan", prompt, null, out _);

        Assert.True(ok);
        var command = CommandOf(_started[0]);
        Assert.DoesNotContain(prompt, command, StringComparison.Ordinal);
        Assert.Contains("--permission-mode plan", command, StringComparison.Ordinal);
    }

    [Fact]
    public void An_executable_path_with_spaces_is_quoted()
    {
        Create().TryLaunch(@"C:\Program Files\claude.exe", @"C:\project", "plan", null, null, out _);

        Assert.Contains(@"""C:\Program Files\claude.exe""", CommandOf(_started[0]), StringComparison.Ordinal);
    }
}
