using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NightShift.Core.Configuration;
using NightShift.Core.Execution;
using NightShift.Core.History;
using NSubstitute;

namespace NightShift.Core.Tests.Execution;

public sealed class TerminalClaudeRunnerTests : IDisposable
{
    readonly TempDirectory _appData = new();
    readonly TempDirectory _project = new();
    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 26, 22, 0, 0, TimeSpan.Zero));
    readonly RunHistoryStore _history;
    readonly List<ProcessStartInfo> _started = [];

    Func<ProcessStartInfo, bool> _startBehaviour = _ => true;

    public TerminalClaudeRunnerTests() =>
        _history = new RunHistoryStore(new AppPaths(_appData.Path), NullLogger<RunHistoryStore>.Instance);

    sealed class FakeClipboard(bool succeeds) : IClipboard
    {
        public string? Text { get; private set; }

        public Task<bool> TrySetTextAsync(string text, CancellationToken cancellationToken = default)
        {
            Text = text;
            return Task.FromResult(succeeds);
        }
    }

    PilotSettings Settings(Action<PilotSettings>? mutate = null)
    {
        var executable = Path.Combine(_appData.Path, "claude.cmd");
        if (!File.Exists(executable))
        {
            File.WriteAllText(executable, "@echo off");
        }

        var settings = new PilotSettings
        {
            ProjectDirectory = _project.Path,
            ClaudeExecutablePath = executable,
            LaunchMode = LaunchMode.VisibleTerminal,
        };
        mutate?.Invoke(settings);
        return settings;
    }

    TerminalClaudeRunner CreateRunner(IClipboard? clipboard = null)
    {
        var probeRunner = Substitute.For<IAutoModeProbeRunner>();
        probeRunner
            .RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new AutoModeProbeOutput(0, "{\"allow\":[]}", string.Empty, false));

        return new TerminalClaudeRunner(
            new ClaudeExecutableLocator(NullLogger<ClaudeExecutableLocator>.Instance),
            new PromptBuilder(),
            new AutoModeProbe(probeRunner, NullLogger<AutoModeProbe>.Instance, "claude", _time),
            new WorkspaceTrustManager(
                NullLogger<WorkspaceTrustManager>.Instance,
                Path.Combine(_appData.Path, "claude-config.json")),
            _history,
            NullLogger<TerminalClaudeRunner>.Instance,
            clipboard,
            _time,
            info =>
            {
                _started.Add(info);
                return _startBehaviour(info);
            });
    }

    [Fact]
    public async Task A_launched_run_is_recorded_as_Launched_with_no_transcript()
    {
        var record = await CreateRunner().RunAsync(Settings());

        Assert.Equal(RunOutcome.Launched, record.Outcome);
        Assert.Null(record.TranscriptPath);
        Assert.Equal(PermissionMode.Auto, record.PermissionModeUsed);
    }

    [Fact]
    public async Task The_prompt_is_written_into_the_project_handoff_directory()
    {
        await CreateRunner().RunAsync(Settings());

        var path = Path.Combine(_project.Path, TerminalClaudeRunner.HandoffDirectoryName, TerminalClaudeRunner.PromptFileName);
        Assert.True(File.Exists(path));

        var prompt = await File.ReadAllTextAsync(path);
        Assert.StartsWith("/caveman:caveman full", prompt, StringComparison.Ordinal);
        Assert.Contains("plan.md", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_prompt_is_copied_to_the_clipboard_when_one_is_available()
    {
        var clipboard = new FakeClipboard(succeeds: true);

        var record = await CreateRunner(clipboard).RunAsync(Settings());

        Assert.NotNull(clipboard.Text);
        Assert.Contains("copied to the clipboard", record.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_clipboard_copy_does_not_fail_the_run()
    {
        var record = await CreateRunner(new FakeClipboard(succeeds: false)).RunAsync(Settings());

        Assert.Equal(RunOutcome.Launched, record.Outcome);
        Assert.Contains("clipboard copy failed", record.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Windows_Terminal_is_preferred()
    {
        await CreateRunner().RunAsync(Settings());

        Assert.Single(_started);
        Assert.Equal("wt.exe", _started[0].FileName);
        Assert.Equal(_project.Path, _started[0].WorkingDirectory);
        Assert.Contains("--permission-mode auto", string.Join(" ", _started[0].ArgumentList), StringComparison.Ordinal);
    }

    [Fact]
    public async Task It_falls_back_to_cmd_when_Windows_Terminal_is_missing()
    {
        _startBehaviour = info => info.FileName != "wt.exe";

        var record = await CreateRunner().RunAsync(Settings());

        Assert.Equal(2, _started.Count);
        Assert.Equal("cmd.exe", _started[1].FileName);
        Assert.Contains("--permission-mode auto", _started[1].Arguments, StringComparison.Ordinal);
        Assert.Equal(RunOutcome.Launched, record.Outcome);
    }

    [Fact]
    public async Task When_no_terminal_can_be_opened_the_run_is_a_recorded_failure()
    {
        _startBehaviour = _ => false;

        var record = await CreateRunner().RunAsync(Settings());

        Assert.Equal(RunOutcome.Failed, record.Outcome);
        Assert.Contains("Neither Windows Terminal nor cmd.exe", record.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dry_run_writes_the_prompt_but_opens_nothing()
    {
        var record = await CreateRunner().RunAsync(Settings(s => s.DryRun = true));

        Assert.Empty(_started);
        Assert.Equal(SkipReason.DryRun, record.SkipReason);
        Assert.True(File.Exists(
            Path.Combine(_project.Path, TerminalClaudeRunner.HandoffDirectoryName, TerminalClaudeRunner.PromptFileName)));
    }

    [Fact]
    public async Task Trust_is_applied_before_the_window_opens()
    {
        // Unlike headless mode, the interactive UI really does show the trust dialog.
        var configPath = Path.Combine(_appData.Path, "claude-config.json");

        await CreateRunner().RunAsync(Settings(s => s.AutoTrustProjectFolder = true));

        Assert.True(File.Exists(configPath));
        var json = await File.ReadAllTextAsync(configPath);
        Assert.Contains("hasTrustDialogAccepted", json, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _appData.Dispose();
        _project.Dispose();
    }
}
