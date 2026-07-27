using System.Diagnostics;
using NightShift.Core.Execution;

namespace NightShift.Core.Tests.Execution;

public sealed class ClaudeProcessLauncherTests
{
    const string ChildMarker = "CLAUDE_CODE_CHILD_SESSION";
    const string SessionMarker = "CLAUDE_CODE_SESSION_ID";
    const string ConfigDir = "CLAUDE_CONFIG_DIR";

    /// <summary>Sets an environment variable for the duration of a test and puts it back.</summary>
    sealed class TemporaryEnvironment : IDisposable
    {
        readonly List<(string Name, string? Original)> _saved = [];

        public TemporaryEnvironment Set(string name, string? value)
        {
            _saved.Add((name, Environment.GetEnvironmentVariable(name)));
            Environment.SetEnvironmentVariable(name, value);
            return this;
        }

        public void Dispose()
        {
            foreach (var (name, original) in _saved)
            {
                Environment.SetEnvironmentVariable(name, original);
            }
        }
    }

    [Fact]
    public void A_parent_session_marker_is_removed_from_the_child_environment()
    {
        // Launched from inside a Claude Code session, the child inherits that session's identity and
        // silently stops persisting its transcript — the one artefact an unattended run leaves.
        using var environment = new TemporaryEnvironment()
            .Set(ChildMarker, "1")
            .Set(SessionMarker, "parent-session-id");

        var startInfo = new ProcessStartInfo("claude");
        ClaudeProcessLauncher.ScrubInheritedSessionMarkers(startInfo);

        Assert.True(startInfo.Environment.ContainsKey(ChildMarker));
        Assert.Null(startInfo.Environment[ChildMarker]);
        Assert.Null(startInfo.Environment[SessionMarker]);
    }

    [Fact]
    public void Deliberate_configuration_is_left_alone()
    {
        // CLAUDE_CONFIG_DIR is something a user sets on purpose. Scrubbing it would break a
        // deliberately relocated configuration; we remove an accident of parentage, not settings.
        using var environment = new TemporaryEnvironment()
            .Set(ChildMarker, "1")
            .Set(ConfigDir, @"D:\claude-config");

        var startInfo = new ProcessStartInfo("claude");
        ClaudeProcessLauncher.ScrubInheritedSessionMarkers(startInfo);

        // Touching Environment materialises the whole inherited block, so presence proves nothing —
        // the value is what matters. It must still be the one the user set.
        Assert.Equal(@"D:\claude-config", startInfo.Environment[ConfigDir]);
        Assert.Null(startInfo.Environment[ChildMarker]);
    }

    [Fact]
    public void Nothing_is_touched_when_no_marker_is_present()
    {
        using var environment = new TemporaryEnvironment()
            .Set(ChildMarker, null)
            .Set(SessionMarker, null);

        var startInfo = new ProcessStartInfo("claude");
        ClaudeProcessLauncher.ScrubInheritedSessionMarkers(startInfo);

        Assert.False(startInfo.Environment.ContainsKey(ChildMarker));
        Assert.False(startInfo.Environment.ContainsKey(SessionMarker));
    }
}
