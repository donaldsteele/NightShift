using Microsoft.Extensions.Logging.Abstractions;
using NightShift.Core.Configuration;
using NightShift.Core.Execution;

namespace NightShift.Core.Tests.Execution;

/// <summary>
/// Every test runs against a made-up machine (<see cref="FakeExecutableEnvironment"/>), so the
/// result never depends on whether the developer happens to have Claude Code installed. Paths are
/// composed with <see cref="Path.Combine(string, string)"/> rather than written as literals so the
/// suite behaves the same on either separator.
/// </summary>
public sealed class ClaudeExecutableLocatorTests
{
    static readonly string Root = Path.Combine(Path.GetTempPath(), "nightshift-locator");

    readonly FakeExecutableEnvironment _environment = new();

    ClaudeExecutableLocator CreateLocator() =>
        new(NullLogger<ClaudeExecutableLocator>.Instance, _environment);

    [Fact]
    public void The_configured_path_wins_when_the_file_exists()
    {
        var configured = _environment.AddFile(Root, "custom", "claude.exe");
        _environment.AddFile(Root, "npm", "claude.cmd");
        _environment.SetPath(Path.Combine(Root, "npm"));

        var resolution = CreateLocator().Locate(new PilotSettings { ClaudeExecutablePath = configured });

        Assert.True(resolution.IsFound);
        Assert.Equal(configured, resolution.ExecutablePath);
        Assert.Equal(ClaudeExecutableSource.ConfiguredPath, resolution.Source);
        Assert.Null(resolution.FailureReason);
    }

    [Fact]
    public void A_configured_path_is_trimmed_and_unquoted_before_use()
    {
        var configured = _environment.AddFile(Root, "custom", "claude.exe");

        var resolution = CreateLocator().Locate($"  \"{configured}\"  ");

        Assert.Equal(configured, resolution.ExecutablePath);
        Assert.Equal(ClaudeExecutableSource.ConfiguredPath, resolution.Source);
    }

    [Fact]
    public void A_configured_path_that_no_longer_exists_falls_through_to_PATH()
    {
        var onPath = _environment.AddFile(Root, "npm", "claude.cmd");
        _environment.SetPath(Path.Combine(Root, "npm"));

        var resolution = CreateLocator().Locate(Path.Combine(Root, "moved-away", "claude.exe"));

        Assert.Equal(onPath, resolution.ExecutablePath);
        Assert.Equal(ClaudeExecutableSource.SearchPath, resolution.Source);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unset_configured_path_is_not_probed_at_all(string? configured)
    {
        var resolution = CreateLocator().Locate(configured);

        Assert.False(resolution.IsFound);
        Assert.DoesNotContain(resolution.ProbedPaths, path => path.Contains("moved-away", StringComparison.Ordinal));
        Assert.DoesNotContain("configured", resolution.FailureReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_npm_cmd_shim_is_preferred_over_an_exe_in_the_same_directory()
    {
        var directory = Path.Combine(Root, "npm");
        var exe = _environment.AddFile(directory, "claude.exe");
        var cmd = _environment.AddFile(directory, "claude.cmd");
        _environment.SetPath(directory);

        var resolution = CreateLocator().Locate(configuredPath: null);

        Assert.Equal(cmd, resolution.ExecutablePath);
        Assert.NotEqual(exe, resolution.ExecutablePath);
        Assert.True(resolution.RequiresCommandShell);
    }

    [Fact]
    public void PATH_directories_are_searched_in_order()
    {
        var first = Path.Combine(Root, "first");
        var second = Path.Combine(Root, "second");
        var firstHit = _environment.AddFile(first, "claude.exe");
        _environment.AddFile(second, "claude.cmd");
        _environment.SetPath(first, second);

        var resolution = CreateLocator().Locate(configuredPath: null);

        // The earlier directory wins even though the later one holds the more-preferred shim: that
        // is what the user's own shell would run.
        Assert.Equal(firstHit, resolution.ExecutablePath);
        Assert.False(resolution.RequiresCommandShell);
    }

    [Fact]
    public void PATHEXT_extensions_beyond_the_two_shim_names_are_probed()
    {
        var directory = Path.Combine(Root, "tools");
        var bat = _environment.AddFile(directory, "claude.bat");
        _environment.SetPath(directory);
        _environment.Variables["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";

        var resolution = CreateLocator().Locate(configuredPath: null);

        Assert.Equal(bat, resolution.ExecutablePath);
        Assert.True(resolution.RequiresCommandShell);
    }

    [Fact]
    public void A_PATHEXT_entry_without_its_dot_is_tolerated()
    {
        var directory = Path.Combine(Root, "tools");
        var ps1 = _environment.AddFile(directory, "claude.ps1");
        _environment.SetPath(directory);
        _environment.Variables["PATHEXT"] = "EXE;PS1";

        Assert.Equal(ps1, CreateLocator().Locate(configuredPath: null).ExecutablePath);
    }

    [Fact]
    public void An_extensionless_claude_on_PATH_is_the_last_windows_resort()
    {
        var directory = Path.Combine(Root, "shell");
        var bare = _environment.AddFile(directory, "claude");
        _environment.SetPath(directory);

        var resolution = CreateLocator().Locate(configuredPath: null);

        Assert.Equal(bare, resolution.ExecutablePath);
        Assert.Equal(ClaudeExecutableSource.SearchPath, resolution.Source);
        Assert.False(resolution.RequiresCommandShell);
    }

    [Fact]
    public void Quoted_and_duplicated_PATH_entries_are_handled()
    {
        var directory = Path.Combine(Root, "Program Files", "npm");
        var cmd = _environment.AddFile(directory, "claude.cmd");
        _environment.Variables["PATH"] = string.Join(
            Path.PathSeparator,
            $"\"{directory}\"",
            directory,
            string.Empty);

        var resolution = CreateLocator().Locate(configuredPath: null);

        Assert.Equal(cmd, resolution.ExecutablePath);
        Assert.Single(resolution.ProbedPaths, p => p.Equals(cmd, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Only_the_bare_name_is_probed_off_windows()
    {
        var directory = Path.Combine(Root, "usr-local-bin");
        _environment.IsWindows = false;
        _environment.AddFile(directory, "claude.cmd");
        var bare = _environment.AddFile(directory, "claude");
        _environment.SetPath(directory);

        var resolution = CreateLocator().Locate(configuredPath: null);

        Assert.Equal(bare, resolution.ExecutablePath);
        Assert.Equal(new[] { Path.Combine(directory, "claude") }, resolution.ProbedPaths);
    }

    [Fact]
    public void The_npm_prefix_is_probed_when_PATH_has_nothing()
    {
        var appData = Path.Combine(Root, "AppData", "Roaming");
        _environment.Folders[Environment.SpecialFolder.ApplicationData] = appData;
        var cmd = _environment.AddFile(appData, "npm", "claude.cmd");

        var resolution = CreateLocator().Locate(configuredPath: null);

        Assert.Equal(cmd, resolution.ExecutablePath);
        Assert.Equal(ClaudeExecutableSource.WellKnownLocation, resolution.Source);
        Assert.True(resolution.RequiresCommandShell);
    }

    [Fact]
    public void The_native_installer_location_is_probed()
    {
        var localAppData = Path.Combine(Root, "AppData", "Local");
        _environment.Folders[Environment.SpecialFolder.LocalApplicationData] = localAppData;
        var exe = _environment.AddFile(localAppData, "Programs", "claude", "claude.exe");

        var resolution = CreateLocator().Locate(configuredPath: null);

        Assert.Equal(exe, resolution.ExecutablePath);
        Assert.Equal(ClaudeExecutableSource.WellKnownLocation, resolution.Source);
        Assert.False(resolution.RequiresCommandShell);
    }

    [Fact]
    public void The_local_bin_location_is_probed()
    {
        var home = Path.Combine(Root, "home");
        _environment.IsWindows = false;
        _environment.Folders[Environment.SpecialFolder.UserProfile] = home;
        var bin = _environment.AddFile(home, ".local", "bin", "claude");

        var resolution = CreateLocator().Locate(configuredPath: null);

        Assert.Equal(bin, resolution.ExecutablePath);
        Assert.Equal(ClaudeExecutableSource.WellKnownLocation, resolution.Source);
    }

    [Fact]
    public void PATH_beats_a_known_install_location()
    {
        var directory = Path.Combine(Root, "on-path");
        var onPath = _environment.AddFile(directory, "claude.cmd");
        _environment.SetPath(directory);

        var appData = Path.Combine(Root, "AppData", "Roaming");
        _environment.Folders[Environment.SpecialFolder.ApplicationData] = appData;
        _environment.AddFile(appData, "npm", "claude.cmd");

        Assert.Equal(onPath, CreateLocator().Locate(configuredPath: null).ExecutablePath);
    }

    [Fact]
    public void A_special_folder_the_platform_does_not_have_is_skipped()
    {
        _environment.Folders[Environment.SpecialFolder.ApplicationData] = string.Empty;
        _environment.Folders[Environment.SpecialFolder.LocalApplicationData] = string.Empty;
        _environment.Folders[Environment.SpecialFolder.UserProfile] = string.Empty;

        var resolution = CreateLocator().Locate(configuredPath: null);

        Assert.False(resolution.IsFound);
        Assert.Empty(resolution.ProbedPaths);
    }

    [Fact]
    public void Nothing_found_reports_why_rather_than_throwing()
    {
        var missing = Path.Combine(Root, "moved-away", "claude.exe");
        _environment.SetPath(Path.Combine(Root, "empty"));
        _environment.Folders[Environment.SpecialFolder.ApplicationData] = Path.Combine(Root, "AppData", "Roaming");

        var resolution = CreateLocator().Locate(missing);

        Assert.False(resolution.IsFound);
        Assert.Null(resolution.ExecutablePath);
        Assert.Equal(ClaudeExecutableSource.NotFound, resolution.Source);
        Assert.False(resolution.RequiresCommandShell);

        Assert.NotNull(resolution.FailureReason);
        Assert.Contains("not found", resolution.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(missing, resolution.FailureReason, StringComparison.Ordinal);
        Assert.Contains("claude.cmd", resolution.FailureReason, StringComparison.Ordinal);

        // The probe list is what turns "not found" into something actionable.
        Assert.Contains(missing, resolution.ProbedPaths);
        Assert.Contains(Path.Combine(Root, "empty", "claude.cmd"), resolution.ProbedPaths);
        Assert.Contains(Path.Combine(Root, "AppData", "Roaming", "npm", "claude.cmd"), resolution.ProbedPaths);
    }

    [Fact]
    public void A_settings_record_is_required()
    {
        var locator = CreateLocator();

        Assert.Throws<ArgumentNullException>(() => locator.Locate((PilotSettings)null!));
    }

    [Fact]
    public void A_nonsense_configured_path_costs_only_that_candidate()
    {
        var directory = Path.Combine(Root, "npm");
        var cmd = _environment.AddFile(directory, "claude.cmd");
        _environment.SetPath(directory);

        // Long enough to fail Path.GetFullPath on every platform this ships to.
        var resolution = CreateLocator().Locate(new string('x', 8_000));

        Assert.Equal(cmd, resolution.ExecutablePath);
    }

    [Fact]
    public void The_default_environment_is_the_real_machine()
    {
        // Constructing without an environment must not throw; what it finds is machine-dependent,
        // so nothing is asserted about the result itself.
        var locator = new ClaudeExecutableLocator(NullLogger<ClaudeExecutableLocator>.Instance);

        Assert.NotNull(locator.Locate(new PilotSettings()));
    }

    /// <summary>An invented machine: its own PATH, PATHEXT, special folders and set of files.</summary>
    sealed class FakeExecutableEnvironment : IExecutableEnvironment
    {
        readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);

        public bool IsWindows { get; set; } = true;

        public Dictionary<string, string?> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<Environment.SpecialFolder, string> Folders { get; } = [];

        /// <summary>Registers a file and returns its full path, for use as the expected value.</summary>
        public string AddFile(string directory, params string[] segments)
        {
            var path = Path.Combine([directory, .. segments]);
            _files.Add(path);
            return path;
        }

        public void SetPath(params string[] directories) =>
            Variables["PATH"] = string.Join(Path.PathSeparator, directories);

        public string? GetEnvironmentVariable(string name) =>
            Variables.TryGetValue(name, out var value) ? value : null;

        public string GetFolderPath(Environment.SpecialFolder folder) =>
            Folders.TryGetValue(folder, out var path) ? path : string.Empty;

        public bool FileExists(string path) => _files.Contains(path);
    }
}
