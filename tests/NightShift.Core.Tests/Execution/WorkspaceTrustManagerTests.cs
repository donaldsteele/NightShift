using System.Text.Json.Nodes;
using NightShift.Core.Execution;
using Microsoft.Extensions.Logging.Abstractions;

namespace NightShift.Core.Tests.Execution;

/// <summary>
/// Everything here runs against a <c>.claude.json</c> inside a <see cref="TempDirectory"/>.
/// <b>No test in this file may read, write, move or delete the real <c>~/.claude.json</c></b> —
/// corrupting a developer's Claude Code configuration is the worst thing this codebase could do
/// (plan.md §11.4b). The only reference to the real location is an equality check on a string.
/// </summary>
public sealed class WorkspaceTrustManagerTests : IDisposable
{
    /// <summary>
    /// Shaped like a real <c>~/.claude.json</c>: an existing project entry, MCP servers, nested
    /// objects, arrays, a null, unicode and HTML-ish characters, an integer too large for
    /// <c>long</c>, and a high-precision double. Every one of these must come back untouched.
    /// </summary>
    const string RealisticConfig = """
        {
          "numStartups": 137,
          "installMethod": "native",
          "autoUpdates": true,
          "theme": "dark-daltonized",
          "userID": "7d4f0c2e9b1a4e5fa0c3d2b1e0f9a8b7",
          "firstStartTime": "2025-11-02T09:14:22.145Z",
          "oauthAccount": {
            "accountUuid": "0f8a6b3c-1d2e-4f50-9a7b-6c5d4e3f2a1b",
            "emailAddress": "someone@example.com",
            "organizationRole": "admin",
            "workspaceRole": null
          },
          "tipsHistory": {
            "new-user-warmup": 1,
            "shift-enter": 12,
            "memory-command": 3
          },
          "mcpServers": {
            "github": {
              "command": "npx",
              "args": ["-y", "@modelcontextprotocol/server-github"],
              "env": { "GITHUB_TOKEN": "ghp_not_a_real_token" }
            }
          },
          "cachedChangelog": "# Changelog\n\n## 2.1.220\n- Fixed <trust> & \"quoting\" issues",
          "fallbackAvailableWarningThreshold": 0.5,
          "hugeCounter": 12345678901234567890,
          "preciseRatio": 0.1234567890123456789,
          "neverSet": null,
          "emptyList": [],
          "unicodeNote": "café — 日本語 ✅",
          "projects": {
            "C:/code/SomethingElse": {
              "allowedTools": ["Bash", "Read"],
              "history": [
                { "display": "hello", "pastedContents": {} },
                { "display": "world", "pastedContents": { "1": { "id": 1 } } }
              ],
              "hasTrustDialogAccepted": true,
              "lastCost": 0.01234,
              "mcpServers": {}
            }
          },
          "lastReleaseNotesSeen": "2.1.220"
        }
        """;

    readonly TempDirectory _temp = new();

    string ConfigPath => Path.Combine(_temp.Path, ".claude.json");

    string BackupPath => ConfigPath + WorkspaceTrustManager.BackupSuffix;

    string ProjectDirectory => Path.Combine(_temp.Path, "project");

    public void Dispose() => _temp.Dispose();

    WorkspaceTrustManager CreateManager() =>
        new(NullLogger<WorkspaceTrustManager>.Instance, ConfigPath);

    void WriteConfig(string contents) => File.WriteAllText(ConfigPath, contents);

    JsonObject ReadConfig() =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(ConfigPath))!;

    static JsonObject Projects(JsonObject root) => (JsonObject)root["projects"]!;

    static void AssertFullyTrusted(JsonObject projects, string key)
    {
        var project = Assert.IsType<JsonObject>(projects[key]);
        foreach (var flag in WorkspaceTrustManager.TrustFlags)
        {
            Assert.True(project[flag]!.GetValue<bool>(), $"{key} is missing {flag}");
        }
    }

    // ── The thing that must never break: everything else in the file survives ───────────────────

    [Fact]
    public async Task Every_unrelated_key_survives_the_round_trip()
    {
        WriteConfig(RealisticConfig);
        var before = (JsonObject)JsonNode.Parse(RealisticConfig)!;

        var result = await CreateManager().ApplyAsync(ProjectDirectory);

        Assert.Equal(WorkspaceTrustOutcome.Applied, result.Outcome);
        var after = ReadConfig();

        // Same top-level keys, in the same order, with nothing added or dropped.
        Assert.Equal(
            before.Select(pair => pair.Key).ToArray(),
            after.Select(pair => pair.Key).ToArray());

        foreach (var (name, value) in before)
        {
            if (name == "projects")
            {
                continue;
            }

            Assert.True(
                JsonNode.DeepEquals(value, after[name]),
                $"`{name}` changed: {value?.ToJsonString()} -> {after[name]?.ToJsonString()}");
        }

        // The unrelated project entry — arrays, nested objects and all — is byte-for-byte in meaning.
        Assert.True(JsonNode.DeepEquals(
            Projects(before)["C:/code/SomethingElse"],
            Projects(after)["C:/code/SomethingElse"]));
    }

    [Fact]
    public async Task Both_separator_forms_of_the_key_are_written()
    {
        WriteConfig(RealisticConfig);

        var result = await CreateManager().ApplyAsync(ProjectDirectory);

        var projects = Projects(ReadConfig());
        foreach (var key in WorkspaceTrustManager.TrustKeysFor(ProjectDirectory))
        {
            Assert.Contains(key, result.Keys);
            AssertFullyTrusted(projects, key);
        }

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(2, result.Keys.Count);
            Assert.Contains(result.Keys, key => key.Contains('\\'));
            Assert.Contains(result.Keys, key => key.Contains('/') && !key.Contains('\\'));
        }
    }

    /// <summary>
    /// Pinned against a real machine (Claude Code 2.1.220, 2026-07-26) whose <c>~/.claude.json</c>
    /// held one folder twice — <c>C:\code\TrestleBoard</c> and <c>C:/code/TrestleBoard</c>. Trusting
    /// it, with a trailing separator for good measure, must update both entries and add nothing.
    /// </summary>
    [Fact]
    public async Task Existing_mixed_separator_duplicates_are_both_updated_and_nothing_is_added()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows path separator semantics.");

        WriteConfig("""
            {
              "projects": {
                "C:\\code\\TrestleBoard": { "allowedTools": [] },
                "C:/code/TrestleBoard": { "allowedTools": [], "hasTrustDialogAccepted": false }
              }
            }
            """);

        var result = await CreateManager().ApplyAsync(@"C:\code\TrestleBoard\");

        Assert.Equal(WorkspaceTrustOutcome.Applied, result.Outcome);

        var projects = Projects(ReadConfig());
        Assert.Equal(2, projects.Count);
        AssertFullyTrusted(projects, @"C:\code\TrestleBoard");
        AssertFullyTrusted(projects, "C:/code/TrestleBoard");

        // The pre-existing sibling data is still there.
        Assert.NotNull(projects[@"C:\code\TrestleBoard"]!["allowedTools"]);
    }

    [Fact]
    public async Task A_key_differing_only_in_case_is_updated_in_place()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows path casing semantics.");

        WriteConfig("""
            {
              "projects": {
                "c:\\code\\CASEtest": { "exampleFiles": ["a.cs"] },
                "c:/code/casetest/": {}
              }
            }
            """);

        var result = await CreateManager().ApplyAsync(@"C:\code\Casetest");

        Assert.Equal(WorkspaceTrustOutcome.Applied, result.Outcome);

        var projects = Projects(ReadConfig());
        Assert.Equal(2, projects.Count);
        AssertFullyTrusted(projects, @"c:\code\CASEtest");
        AssertFullyTrusted(projects, "c:/code/casetest/");
        Assert.NotNull(projects[@"c:\code\CASEtest"]!["exampleFiles"]);
    }

    [Fact]
    public void Trust_keys_are_absolute_and_lose_their_trailing_separator()
    {
        var keys = WorkspaceTrustManager.TrustKeysFor(ProjectDirectory + Path.DirectorySeparatorChar);

        Assert.All(keys, key => Assert.False(key.EndsWith('/') || key.EndsWith('\\')));
        Assert.Contains(Path.GetFullPath(ProjectDirectory), keys);
    }

    // ── Missing / absent / malformed input ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_missing_projects_object_is_created()
    {
        WriteConfig("""{ "numStartups": 3, "theme": "dark" }""");

        var result = await CreateManager().ApplyAsync(ProjectDirectory);

        Assert.Equal(WorkspaceTrustOutcome.Applied, result.Outcome);

        var root = ReadConfig();
        Assert.Equal(3, root["numStartups"]!.GetValue<int>());
        Assert.Equal("dark", root["theme"]!.GetValue<string>());
        AssertFullyTrusted(Projects(root), Path.GetFullPath(ProjectDirectory));
    }

    [Fact]
    public async Task A_null_projects_value_is_replaced()
    {
        WriteConfig("""{ "projects": null, "theme": "dark" }""");

        var result = await CreateManager().ApplyAsync(ProjectDirectory);

        Assert.Equal(WorkspaceTrustOutcome.Applied, result.Outcome);
        AssertFullyTrusted(Projects(ReadConfig()), Path.GetFullPath(ProjectDirectory));
    }

    [Fact]
    public async Task An_absent_config_file_is_created_with_no_backup()
    {
        var result = await CreateManager().ApplyAsync(ProjectDirectory);

        Assert.Equal(WorkspaceTrustOutcome.Applied, result.Outcome);
        Assert.Null(result.BackupPath);
        Assert.False(File.Exists(BackupPath));
        AssertFullyTrusted(Projects(ReadConfig()), Path.GetFullPath(ProjectDirectory));
    }

    [Fact]
    public async Task A_malformed_file_is_reported_and_left_exactly_as_it_was()
    {
        const string Garbage = "{ \"projects\": { \"C:/x\": { oops not json";
        WriteConfig(Garbage);
        var before = File.ReadAllBytes(ConfigPath);

        var result = await CreateManager().ApplyAsync(ProjectDirectory);

        Assert.Equal(WorkspaceTrustOutcome.Failed, result.Outcome);
        Assert.False(result.IsTrusted);
        Assert.NotNull(result.Error);
        Assert.Equal(Garbage, File.ReadAllText(ConfigPath));
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));

        // Crucially: no backup either. Backing up implies moving the file, and this one stays put.
        Assert.False(File.Exists(BackupPath));
        Assert.Single(Directory.GetFiles(_temp.Path));
    }

    [Fact]
    public async Task A_root_that_is_not_an_object_is_refused()
    {
        WriteConfig("[1, 2, 3]");

        var result = await CreateManager().ApplyAsync(ProjectDirectory);

        Assert.Equal(WorkspaceTrustOutcome.Failed, result.Outcome);
        Assert.Equal("[1, 2, 3]", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public async Task A_projects_property_that_is_not_an_object_is_refused()
    {
        const string Odd = """{ "projects": ["C:/code/x"] }""";
        WriteConfig(Odd);

        var result = await CreateManager().ApplyAsync(ProjectDirectory);

        Assert.Equal(WorkspaceTrustOutcome.Failed, result.Outcome);
        Assert.Equal(Odd, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public async Task A_project_entry_that_is_not_an_object_is_refused()
    {
        var key = Path.GetFullPath(ProjectDirectory).Replace('\\', '/');
        var odd = $$"""{ "projects": { "{{key}}": "trusted, honest" } }""";
        WriteConfig(odd);

        var result = await CreateManager().ApplyAsync(ProjectDirectory);

        Assert.Equal(WorkspaceTrustOutcome.Failed, result.Outcome);
        Assert.Equal(odd, File.ReadAllText(ConfigPath));
    }

    // ── Backup behaviour ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_backup_is_taken_once_and_holds_the_original_file()
    {
        WriteConfig(RealisticConfig);
        var manager = CreateManager();

        var first = await manager.ApplyAsync(ProjectDirectory);
        var second = await manager.ApplyAsync(Path.Combine(_temp.Path, "another-project"));

        Assert.Equal(WorkspaceTrustOutcome.Applied, first.Outcome);
        Assert.Equal(WorkspaceTrustOutcome.Applied, second.Outcome);
        Assert.Equal(BackupPath, first.BackupPath);
        Assert.Equal(BackupPath, second.BackupPath);

        // Exactly two files: the config and one backup. No `.nightshift.bak-1`, no temp leftovers.
        Assert.Equal(2, Directory.GetFiles(_temp.Path).Length);
        Assert.Equal(RealisticConfig, File.ReadAllText(BackupPath));

        // And both projects made it into the live file.
        var projects = Projects(ReadConfig());
        AssertFullyTrusted(projects, Path.GetFullPath(ProjectDirectory));
        AssertFullyTrusted(projects, Path.GetFullPath(Path.Combine(_temp.Path, "another-project")));
    }

    [Fact]
    public async Task Re_applying_an_already_trusted_folder_writes_nothing()
    {
        WriteConfig(RealisticConfig);
        var manager = CreateManager();
        await manager.ApplyAsync(ProjectDirectory);

        var written = File.ReadAllText(ConfigPath);
        var writtenAt = File.GetLastWriteTimeUtc(ConfigPath);

        var again = await manager.ApplyAsync(ProjectDirectory);

        Assert.Equal(WorkspaceTrustOutcome.AlreadyTrusted, again.Outcome);
        Assert.True(again.IsTrusted);
        Assert.Equal(written, File.ReadAllText(ConfigPath));
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(ConfigPath));
        Assert.Equal(2, Directory.GetFiles(_temp.Path).Length);
    }

    // ── Checking without writing ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Check_reports_untrusted_then_trusted_and_never_writes()
    {
        WriteConfig(RealisticConfig);
        var manager = CreateManager();

        var before = await manager.CheckAsync(ProjectDirectory);

        Assert.False(before.IsTrusted);
        Assert.Equal(before.Keys, before.MissingKeys);
        Assert.Null(before.Problem);
        Assert.Equal(RealisticConfig, File.ReadAllText(ConfigPath));
        Assert.Single(Directory.GetFiles(_temp.Path));

        await manager.ApplyAsync(ProjectDirectory);
        var after = await manager.CheckAsync(ProjectDirectory);

        Assert.True(after.IsTrusted);
        Assert.Empty(after.MissingKeys);
    }

    [Fact]
    public async Task Check_reports_one_missing_form_when_only_the_other_is_present()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows path separator semantics.");

        var native = Path.GetFullPath(ProjectDirectory);
        WriteConfig($$"""
            {
              "projects": {
                "{{native.Replace("\\", "\\\\")}}": {
                  "hasTrustDialogAccepted": true,
                  "hasTrustDialogHooksAccepted": true,
                  "hasCompletedProjectOnboarding": true
                }
              }
            }
            """);

        var status = await CreateManager().CheckAsync(ProjectDirectory);

        Assert.False(status.IsTrusted);
        Assert.Equal(native.Replace('\\', '/'), Assert.Single(status.MissingKeys));
    }

    [Fact]
    public async Task Check_reports_a_problem_for_an_absent_file()
    {
        var status = await CreateManager().CheckAsync(ProjectDirectory);

        Assert.False(status.IsTrusted);
        Assert.NotNull(status.Problem);
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public async Task Check_reports_a_problem_for_a_malformed_file_without_touching_it()
    {
        WriteConfig("not json at all");

        var status = await CreateManager().CheckAsync(ProjectDirectory);

        Assert.False(status.IsTrusted);
        Assert.NotNull(status.Problem);
        Assert.Equal("not json at all", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public async Task Flags_stored_as_strings_still_count_as_trusted()
    {
        var key = Path.GetFullPath(ProjectDirectory).Replace('\\', '/');
        WriteConfig($$"""
            {
              "projects": {
                "{{key}}": {
                  "hasTrustDialogAccepted": "true",
                  "hasTrustDialogHooksAccepted": "true",
                  "hasCompletedProjectOnboarding": "true"
                }
              }
            }
            """);

        var status = await CreateManager().CheckAsync(ProjectDirectory);

        // On Windows the backslash form is still missing; the forward-slash form is satisfied.
        Assert.DoesNotContain(key, status.MissingKeys);
    }

    // ── Restore ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Restore_puts_the_original_file_back_and_keeps_the_backup()
    {
        WriteConfig(RealisticConfig);
        var manager = CreateManager();
        await manager.ApplyAsync(ProjectDirectory);
        Assert.NotEqual(RealisticConfig, File.ReadAllText(ConfigPath));

        var result = await manager.RestoreBackupAsync();

        Assert.True(result.Restored);
        Assert.Null(result.Error);
        Assert.Equal(RealisticConfig, File.ReadAllText(ConfigPath));
        Assert.True(File.Exists(BackupPath));
    }

    [Fact]
    public async Task Restore_without_a_backup_reports_an_error_and_leaves_the_file_alone()
    {
        WriteConfig(RealisticConfig);

        var result = await CreateManager().RestoreBackupAsync();

        Assert.False(result.Restored);
        Assert.NotNull(result.Error);
        Assert.Equal(RealisticConfig, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public async Task Restore_refuses_a_backup_that_is_not_valid_json()
    {
        WriteConfig(RealisticConfig);
        File.WriteAllText(BackupPath, "half a file {");

        var result = await CreateManager().RestoreBackupAsync();

        Assert.False(result.Restored);
        Assert.NotNull(result.Error);
        Assert.Equal(RealisticConfig, File.ReadAllText(ConfigPath));
    }

    // ── Trust-blocked detection (detection only; see the note on the manager) ───────────────────

    [Theory]
    [InlineData(false, "Do you trust the files in this folder?", true)]
    [InlineData(false, "error: workspace is not trusted", true)]
    [InlineData(false, "some unrelated failure", false)]
    [InlineData(false, "", false)]
    [InlineData(false, null, false)]
    [InlineData(true, "Do you trust the files in this folder?", false)]
    public void Trust_blocked_detection_needs_both_no_init_event_and_trust_wording(
        bool sawInitEvent,
        string? output,
        bool expected) =>
        Assert.Equal(expected, WorkspaceTrustManager.LooksTrustBlocked(sawInitEvent, output));

    // ── The real path is only ever named, never opened ─────────────────────────────────────────

    [Fact]
    public void The_default_config_path_is_dot_claude_json_in_the_user_profile()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude.json");

        Assert.Equal(expected, WorkspaceTrustManager.DefaultConfigPath);
    }

    [Fact]
    public void The_backup_suffix_is_the_one_the_restore_button_looks_for() =>
        Assert.Equal(".nightshift.bak", WorkspaceTrustManager.BackupSuffix);
    // -- Concurrent writers (plan.md section 5.3.1) --------------------------------------------

    [Fact]
    public async Task Each_apply_reads_fresh_state_so_another_writer_is_never_rolled_back()
    {
        // Deterministic stand-in for the real hazard: Claude Code rewrites this file continuously,
        // so anything cached from an earlier read is already stale. Writing back a remembered
        // document would roll their change back.
        WriteConfig("""{ "numStartups": 1, "projects": {} }""");
        var manager = CreateManager();

        var first = await manager.ApplyAsync(ProjectDirectory);
        Assert.True(first.IsTrusted);

        // Somebody else updates the file, preserving what we wrote.
        var external = ReadConfig();
        external["numStartups"] = 2;
        external["theirNewKey"] = "added by the CLI";
        WriteConfig(external.ToJsonString());

        var second = await manager.ApplyAsync(Path.Combine(_temp.Path, "another-project"));
        Assert.True(second.IsTrusted);

        var root = ReadConfig();
        Assert.Equal(2, root["numStartups"]!.GetValue<int>());
        Assert.Equal("added by the CLI", root["theirNewKey"]!.GetValue<string>());

        // Both projects survive: the first apply's keys were not lost by the second.
        Assert.True(Projects(root).Count >= 4);
    }

    [Fact]
    public async Task A_file_that_never_settles_is_refused_rather_than_clobbered()
    {
        WriteConfig("""{ "projects": {} }""");
        var manager = CreateManager();

        using var cts = new CancellationTokenSource();
        var churn = Task.Run(async () =>
        {
            var n = 0;
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    File.WriteAllText(ConfigPath, $$"""{ "n": {{n++}}, "projects": {} }""");
                }
                catch (IOException)
                {
                }

                await Task.Delay(1, CancellationToken.None);
            }
        });

        var result = await manager.ApplyAsync(ProjectDirectory);
        await cts.CancelAsync();
        await churn;

        // Either it squeezed a write in or it gave up, but it must never throw and must never leave
        // the file unparseable.
        Assert.True(result.Outcome is WorkspaceTrustOutcome.Applied or WorkspaceTrustOutcome.Failed);
        Assert.NotNull(ReadConfig()["projects"]);
    }

}
