using NightShift.Core.Preflight;
using Microsoft.Extensions.Logging.Abstractions;

namespace NightShift.Core.Tests.Preflight;

/// <summary>
/// plan.md §5.3.2: <c>permissions.ask</c> rules are evaluated before the auto-mode classifier and
/// always force a prompt, so preflight has to find them or an unattended run just sits there.
/// Every path is injected — nothing here reads the developer's own <c>~/.claude</c>.
/// </summary>
public sealed class PermissionsAskScannerTests : IDisposable
{
    readonly TempDirectory _temp = new();

    string UserSettingsPath => Path.Combine(_temp.Path, "user", ".claude", "settings.json");

    string ProjectDirectory => Path.Combine(_temp.Path, "project");

    string ProjectSettingsPath => Path.Combine(ProjectDirectory, ".claude", "settings.json");

    string ProjectLocalSettingsPath => Path.Combine(ProjectDirectory, ".claude", "settings.local.json");

    public void Dispose() => _temp.Dispose();

    PermissionsAskScanner CreateScanner() =>
        new(NullLogger<PermissionsAskScanner>.Instance, UserSettingsPath);

    static void Write(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    Task<PermissionsScanResult> ScanAsync() => CreateScanner().ScanAsync(ProjectDirectory);

    // ── Where it looks ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void It_reads_the_user_file_and_both_project_files()
    {
        var files = CreateScanner().ResolveFiles(ProjectDirectory);

        Assert.Equal(
            [UserSettingsPath, ProjectSettingsPath, ProjectLocalSettingsPath],
            files.Select(f => f.Path).ToArray());
        Assert.Equal(
            [SettingsScope.User, SettingsScope.Project, SettingsScope.ProjectLocal],
            files.Select(f => f.Scope).ToArray());
    }

    [Fact]
    public async Task With_no_project_picked_only_the_user_file_is_scanned()
    {
        var result = await CreateScanner().ScanAsync(projectDirectory: null);

        Assert.Equal(UserSettingsPath, Assert.Single(result.Files).Path);
    }

    [Fact]
    public void The_default_user_settings_path_is_dot_claude_settings_json()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            "settings.json");

        Assert.Equal(expected, PermissionsAskScanner.DefaultUserSettingsPath);
    }

    // ── Missing and malformed files ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Absent_files_are_reported_and_are_not_a_problem()
    {
        var result = await ScanAsync();

        Assert.Empty(result.Findings);
        Assert.False(result.HasBlockers);
        Assert.Equal(3, result.Files.Count);
        Assert.All(result.Files, file => Assert.False(file.Exists));
    }

    [Fact]
    public async Task A_malformed_file_is_reported_never_thrown()
    {
        Write(UserSettingsPath, "{ \"permissions\": { \"ask\": [ ");

        var result = await ScanAsync();

        var finding = Assert.Single(result.Findings);
        Assert.Equal(PermissionsFindingKind.Unreadable, finding.Kind);
        Assert.Equal(SettingsScope.User, finding.Scope);
        Assert.Equal(UserSettingsPath, finding.FilePath);
        Assert.True(result.HasUnreadableFiles);
        Assert.False(result.HasBlockers);
        Assert.NotNull(result.Files.Single(f => f.Path == UserSettingsPath).Error);
    }

    [Fact]
    public async Task A_settings_file_that_is_not_an_object_is_reported()
    {
        Write(ProjectSettingsPath, "[]");

        var result = await ScanAsync();

        Assert.Equal(PermissionsFindingKind.Unreadable, Assert.Single(result.Findings).Kind);
    }

    [Fact]
    public async Task An_ask_property_that_is_not_an_array_is_reported()
    {
        Write(UserSettingsPath, """{ "permissions": { "ask": "Bash(rm:*)" } }""");

        var result = await ScanAsync();

        var finding = Assert.Single(result.Findings);
        Assert.Equal(PermissionsFindingKind.Unreadable, finding.Kind);
        Assert.Contains("not an array", finding.Rule);
    }

    // ── Ask rules ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Files_without_ask_rules_produce_nothing()
    {
        Write(UserSettingsPath, """
            {
              "model": "claude-opus-5",
              "permissions": { "allow": ["Bash(git status:*)"], "deny": ["Read(./.env)"], "ask": [] }
            }
            """);
        Write(ProjectSettingsPath, """{ "hooks": {}, "permissions": { "allow": [] } }""");
        Write(ProjectLocalSettingsPath, "{}");

        var result = await ScanAsync();

        Assert.Empty(result.Findings);
        Assert.False(result.HasBlockers);
        Assert.All(result.Files, file => Assert.True(file.Exists));
    }

    [Fact]
    public async Task Ask_rules_are_listed_with_their_file_and_scope()
    {
        Write(UserSettingsPath, """
            { "permissions": { "ask": ["Bash(git push:*)", "WebFetch"] } }
            """);
        Write(ProjectSettingsPath, """
            { "permissions": { "allow": ["Read"], "ask": ["Bash(rm:*)"] } }
            """);
        Write(ProjectLocalSettingsPath, """
            { "permissions": { "ask": ["Edit(//secret/**)"] } }
            """);

        var result = await ScanAsync();

        Assert.True(result.HasBlockers);
        Assert.Equal(4, result.AskRules.Count());

        var user = result.AskRules.Where(f => f.Scope == SettingsScope.User).ToArray();
        Assert.Equal(["Bash(git push:*)", "WebFetch"], user.Select(f => f.Rule).ToArray());
        Assert.All(user, f => Assert.Equal(UserSettingsPath, f.FilePath));

        Assert.Equal(
            "Bash(rm:*)",
            Assert.Single(result.AskRules, f => f.Scope == SettingsScope.Project).Rule);
        Assert.Equal(
            "Edit(//secret/**)",
            Assert.Single(result.AskRules, f => f.Scope == SettingsScope.ProjectLocal).Rule);
    }

    [Fact]
    public async Task A_non_string_ask_entry_is_still_listed()
    {
        Write(UserSettingsPath, """{ "permissions": { "ask": [{ "tool": "Bash" }, null] } }""");

        var result = await ScanAsync();

        Assert.Equal(2, result.AskRules.Count());
        Assert.Contains(result.AskRules, f => f.Rule.Contains("Bash"));
        Assert.Contains(result.AskRules, f => f.Rule == "(null)");
    }

    [Fact]
    public async Task A_finding_renders_as_one_readable_line()
    {
        Write(UserSettingsPath, """{ "permissions": { "ask": ["Bash(git push:*)"] } }""");

        var finding = Assert.Single((await ScanAsync()).AskRules);

        Assert.Contains("Bash(git push:*)", finding.ToString());
        Assert.Contains(UserSettingsPath, finding.ToString());
    }

    // ── MCP servers that want a human ──────────────────────────────────────────────────────────

    [Fact]
    public async Task An_mcp_server_requiring_interaction_is_reported()
    {
        Write(UserSettingsPath, """
            {
              "mcpServers": {
                "quiet": { "command": "npx", "requiresUserInteraction": false },
                "loud": { "command": "npx", "requiresUserInteraction": true },
                "stringly": { "command": "npx", "requiresUserInteraction": "true" }
              }
            }
            """);

        var result = await ScanAsync();

        Assert.True(result.HasBlockers);
        Assert.Equal(
            ["loud", "stringly"],
            result.InteractiveMcpServers.Select(f => f.Rule).Order().ToArray());
    }

    [Fact]
    public async Task The_flag_is_found_on_a_nested_tool_entry_too()
    {
        Write(ProjectSettingsPath, """
            {
              "mcpServers": {
                "browser": {
                  "command": "npx",
                  "tools": [
                    { "name": "click" },
                    { "name": "login", "requiresUserInteraction": true }
                  ]
                }
              }
            }
            """);

        var result = await ScanAsync();

        var finding = Assert.Single(result.InteractiveMcpServers);
        Assert.Equal("browser", finding.Rule);
        Assert.Equal(SettingsScope.Project, finding.Scope);
        Assert.Equal(ProjectSettingsPath, finding.FilePath);
    }

    [Fact]
    public async Task Servers_that_need_no_interaction_are_not_reported()
    {
        Write(UserSettingsPath, """
            { "mcpServers": { "github": { "command": "npx", "args": ["-y", "server"] } } }
            """);

        Assert.Empty((await ScanAsync()).Findings);
    }

    [Fact]
    public async Task Ask_rules_and_interactive_servers_are_reported_together()
    {
        Write(UserSettingsPath, """
            {
              "permissions": { "ask": ["Bash(rm:*)"] },
              "mcpServers": { "loud": { "requiresUserInteraction": true } }
            }
            """);

        var result = await ScanAsync();

        Assert.Equal(2, result.Findings.Count);
        Assert.Single(result.AskRules);
        Assert.Single(result.InteractiveMcpServers);
    }

    [Fact]
    public async Task Scanning_writes_nothing()
    {
        Write(UserSettingsPath, """{ "permissions": { "ask": ["Bash(rm:*)"] } }""");
        var before = File.ReadAllText(UserSettingsPath);
        var writtenAt = File.GetLastWriteTimeUtc(UserSettingsPath);

        await ScanAsync();

        Assert.Equal(before, File.ReadAllText(UserSettingsPath));
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(UserSettingsPath));
        Assert.False(Directory.Exists(Path.Combine(ProjectDirectory, ".claude")));
    }
}
