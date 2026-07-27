using NightShift.Core.Configuration;
using NightShift.Core.Execution;
using NightShift.Core.Markdown;
using NightShift.Desktop.ViewModels;

namespace NightShift.Desktop.Tests;

/// <summary>
/// The plan window, driven through its view model.
/// </summary>
/// <remarks>
/// No Avalonia here, as everywhere else in this project: <c>ImmediateUiDispatcher</c> runs every
/// callback inline, so by the time a raise returns the state has already settled.
/// </remarks>
public sealed class PlanDocumentViewModelTests
{
    static ViewModelHarness WithProject(out string planPath, string? content = null)
    {
        var harness = new ViewModelHarness();
        var project = Path.Combine(harness.Root, "project");
        Directory.CreateDirectory(project);

        planPath = Path.Combine(project, "plan.md");
        File.WriteAllText(planPath, content ?? "# Plan\n\n- [x] done\n- [ ] left\n");

        harness.Settings.SaveAsync(new PilotSettings { ProjectDirectory = project }).GetAwaiter().GetResult();
        return harness;
    }

    // ── Loading ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Loading_parses_the_file_and_starts_watching_it()
    {
        using var harness = WithProject(out var planPath);
        var plan = harness.CreatePlan();

        await plan.LoadAsync();

        Assert.NotNull(plan.Document);
        Assert.Contains(plan.Document!.Blocks, block => block is HeadingBlock);
        Assert.Equal(planPath, harness.Watcher.Watching);
        Assert.Equal("1 of 2 items complete", plan.ItemSummary);
        Assert.False(plan.HasError);
    }

    [Fact]
    public async Task Loading_with_no_project_directory_explains_rather_than_throwing()
    {
        using var harness = new ViewModelHarness();
        var plan = harness.CreatePlan();

        await plan.LoadAsync();

        Assert.False(plan.HasPlanPath);
        Assert.True(plan.HasError);
        Assert.Contains("No project directory", plan.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_plan_file_says_so_in_plain_language()
    {
        using var harness = WithProject(out var planPath);
        File.Delete(planPath);

        var plan = harness.CreatePlan();
        await plan.LoadAsync();

        Assert.Contains("no plan.md in this project yet", plan.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ── Editing and saving ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Editing_leaves_the_rendered_document_alone_until_a_save()
    {
        using var harness = WithProject(out _);
        var plan = harness.CreatePlan();
        await plan.LoadAsync();

        var before = plan.Document;
        plan.BeginEditCommand.Execute(null);
        plan.RawText = "# Different\n";

        Assert.True(plan.IsEditing);
        Assert.True(plan.IsDirty);
        Assert.Same(before, plan.Document);
    }

    [Fact]
    public async Task Saving_writes_the_file_reparses_and_leaves_edit_mode()
    {
        using var harness = WithProject(out var planPath);
        var plan = harness.CreatePlan();
        await plan.LoadAsync();

        plan.BeginEditCommand.Execute(null);
        plan.RawText = "# Plan\n\n- [x] done\n- [x] left\n";
        await plan.SaveCommand.ExecuteAsync(null);

        Assert.Equal("# Plan\n\n- [x] done\n- [x] left\n", File.ReadAllText(planPath).Replace("\r\n", "\n"));
        Assert.False(plan.IsEditing);
        Assert.False(plan.IsDirty);
        Assert.Equal("2 of 2 items complete", plan.ItemSummary);
    }

    [Fact]
    public async Task Saving_a_crlf_file_keeps_it_crlf()
    {
        // Without this a two-line edit reports every line in the file as changed at the next
        // `git status`, because the text box normalises everything it holds to LF.
        using var harness = WithProject(out var planPath, "# Plan\r\n\r\n- [ ] one\r\n- [ ] two\r\n");
        var plan = harness.CreatePlan();
        await plan.LoadAsync();

        plan.BeginEditCommand.Execute(null);
        plan.RawText = plan.RawText.Replace("- [ ] one", "- [x] one", StringComparison.Ordinal);
        await plan.SaveCommand.ExecuteAsync(null);

        Assert.Equal("# Plan\r\n\r\n- [x] one\r\n- [ ] two\r\n", File.ReadAllText(planPath));
    }

    [Fact]
    public async Task A_save_leaves_no_temp_file_beside_the_plan()
    {
        using var harness = WithProject(out var planPath);
        var plan = harness.CreatePlan();
        await plan.LoadAsync();

        plan.BeginEditCommand.Execute(null);
        plan.RawText += "\nmore\n";
        await plan.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["plan.md"], Directory.GetFiles(Path.GetDirectoryName(planPath)!).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_save_asks_the_dashboard_to_recount()
    {
        using var harness = WithProject(out _);
        var plan = harness.CreatePlan();
        await plan.LoadAsync();

        var raised = 0;
        plan.Saved += (_, _) => raised++;

        plan.BeginEditCommand.Execute(null);
        plan.RawText = "- [x] done\n";
        await plan.SaveCommand.ExecuteAsync(null);

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task A_failed_save_reports_it_and_keeps_the_buffer()
    {
        using var harness = WithProject(out var planPath);
        var plan = harness.CreatePlan();
        await plan.LoadAsync();

        plan.BeginEditCommand.Execute(null);
        plan.RawText = "# Mine\n";

        // A directory where the file should be: the write fails, the typing must not.
        File.Delete(planPath);
        Directory.CreateDirectory(planPath);

        await plan.SaveCommand.ExecuteAsync(null);

        Assert.True(plan.HasError);
        Assert.Equal("# Mine\n", plan.RawText);
        Assert.True(plan.IsEditing);
    }

    [Fact]
    public async Task Cancelling_a_dirty_edit_asks_first_and_keeps_the_text_when_refused()
    {
        using var harness = WithProject(out _);
        var plan = harness.CreatePlan();
        await plan.LoadAsync();

        plan.BeginEditCommand.Execute(null);
        plan.RawText = "# Mine\n";
        harness.Confirmation.Answer = false;

        await plan.CancelEditCommand.ExecuteAsync(null);

        Assert.Equal(1, harness.Confirmation.CallCount);
        Assert.True(plan.IsEditing);
        Assert.Equal("# Mine\n", plan.RawText);
    }

    // ── The watcher and conflicts ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_change_on_disk_with_a_clean_buffer_reloads_silently()
    {
        using var harness = WithProject(out var planPath);
        var plan = harness.CreatePlan();
        await plan.LoadAsync();

        File.WriteAllText(planPath, "# Plan\n\n- [x] done\n- [x] left\n");
        harness.Watcher.RaiseChanged();
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await plan.PendingReload;

        Assert.False(plan.HasConflict);
        Assert.Equal("2 of 2 items complete", plan.ItemSummary);
        Assert.Contains("changed on disk", plan.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_change_on_disk_during_an_edit_raises_a_conflict_and_keeps_the_buffer()
    {
        using var harness = WithProject(out var planPath);
        var plan = harness.CreatePlan();
        await plan.LoadAsync();

        plan.BeginEditCommand.Execute(null);
        plan.RawText = "# Mine\n\n- [ ] mine\n";

        File.WriteAllText(planPath, "# Theirs\n\n- [x] theirs\n- [x] and another\n");
        harness.Watcher.RaiseChanged();
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await plan.PendingReload;

        Assert.True(plan.HasConflict);
        Assert.Equal("# Mine\n\n- [ ] mine\n", plan.RawText);
        Assert.Contains("The version on disk has 2 of 2", plan.ConflictDetail, StringComparison.Ordinal);
        Assert.Contains("Yours has 0 of 1", plan.ConflictDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Several_notifications_inside_the_debounce_produce_one_reload()
    {
        using var harness = WithProject(out var planPath);
        var plan = harness.CreatePlan();
        await plan.LoadAsync();

        var reloads = 0;
        plan.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlanDocumentViewModel.Document))
            {
                reloads++;
            }
        };

        File.WriteAllText(planPath, "# Changed\n");
        harness.Watcher.RaiseChanged();
        harness.Watcher.RaiseChanged();
        harness.Watcher.RaiseChanged();
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await plan.PendingReload;

        Assert.Equal(1, reloads);
    }

    [Fact]
    public async Task Our_own_save_coming_back_through_the_watcher_is_not_a_conflict()
    {
        // Content comparison rather than a suppression window, so this is exact rather than a race.
        using var harness = WithProject(out _);
        var plan = harness.CreatePlan();
        await plan.LoadAsync();

        plan.BeginEditCommand.Execute(null);
        plan.RawText = "# Saved by me\n";
        await plan.SaveCommand.ExecuteAsync(null);

        harness.Watcher.RaiseChanged();
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await plan.PendingReload;

        Assert.False(plan.HasConflict);
    }

    [Fact]
    public async Task Keep_mine_dismisses_the_conflict_and_the_next_save_wins()
    {
        using var harness = WithProject(out var planPath);
        var plan = harness.CreatePlan();
        await plan.LoadAsync();

        plan.BeginEditCommand.Execute(null);
        plan.RawText = "# Mine\n";
        File.WriteAllText(planPath, "# Theirs\n");
        harness.Watcher.RaiseChanged();
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await plan.PendingReload;

        plan.KeepMineCommand.Execute(null);
        await plan.SaveCommand.ExecuteAsync(null);

        Assert.False(plan.HasConflict);
        Assert.Equal("# Mine\n", File.ReadAllText(planPath).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Load_theirs_asks_before_discarding_and_does_nothing_when_refused()
    {
        using var harness = WithProject(out var planPath);
        var plan = harness.CreatePlan();
        await plan.LoadAsync();

        plan.BeginEditCommand.Execute(null);
        plan.RawText = "# Mine\n";
        File.WriteAllText(planPath, "# Theirs\n");
        harness.Watcher.RaiseChanged();
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await plan.PendingReload;

        harness.Confirmation.Answer = false;
        await plan.LoadTheirsCommand.ExecuteAsync(null);

        Assert.Equal("# Mine\n", plan.RawText);

        harness.Confirmation.Answer = true;
        await plan.LoadTheirsCommand.ExecuteAsync(null);

        Assert.Equal("# Theirs\n", plan.RawText);
    }

    // ── Run state ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Editing_during_a_run_is_allowed_but_says_so()
    {
        using var harness = WithProject(out _);
        var plan = harness.CreatePlan();
        await plan.LoadAsync();

        using var run = await harness.StartHeldRunAsync();
        plan.BeginEditCommand.Execute(null);

        Assert.True(plan.IsRunning);
        Assert.True(plan.IsEditing);
        Assert.Contains("A run is working", plan.StatusMessage, StringComparison.Ordinal);
    }

    // ── Shutdown ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_clean_buffer_flushes_without_asking_anything()
    {
        using var harness = WithProject(out _);
        var plan = harness.CreatePlan();
        await plan.LoadAsync();

        await plan.FlushAsync();

        Assert.Equal(0, harness.Confirmation.CallCount);
    }

    [Fact]
    public async Task An_unsaved_edit_at_shutdown_is_offered_rather_than_written()
    {
        // A plan is a document a background run is also writing to. Saving it silently on exit
        // could overwrite what that run just committed, which is the one outcome nobody can undo.
        using var harness = WithProject(out var planPath);
        var plan = harness.CreatePlan();
        await plan.LoadAsync();

        plan.BeginEditCommand.Execute(null);
        plan.RawText = "# Mine\n";
        harness.Confirmation.Answer = false;

        await plan.FlushAsync();

        Assert.Equal(1, harness.Confirmation.CallCount);
        Assert.DoesNotContain("# Mine", File.ReadAllText(planPath), StringComparison.Ordinal);

        harness.Confirmation.Answer = true;
        await plan.FlushAsync();

        Assert.Equal("# Mine\n", File.ReadAllText(planPath).Replace("\r\n", "\n"));
    }

    // ── Edit with Claude ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Edit_with_Claude_opens_plan_mode_in_the_project_directory()
    {
        using var harness = WithProject(out var planPath);
        harness.Locator.Resolution =
            new ClaudeExecutableResolution(@"C:\claude.cmd", ClaudeExecutableSource.ConfiguredPath, null, []);

        var plan = harness.CreatePlan();
        await plan.LoadAsync();

        await plan.EditWithClaudeCommand.ExecuteAsync(null);

        var launch = Assert.Single(harness.Terminal.Launches);
        Assert.Equal(@"C:\claude.cmd", launch.Executable);
        Assert.Equal(Path.GetDirectoryName(planPath), launch.Directory);
        Assert.Equal("plan", launch.Mode);
        Assert.NotNull(launch.Prompt);
    }

    [Fact]
    public async Task Edit_with_Claude_explains_itself_when_claude_cannot_be_found()
    {
        using var harness = WithProject(out _);
        var plan = harness.CreatePlan();
        await plan.LoadAsync();

        await plan.EditWithClaudeCommand.ExecuteAsync(null);

        Assert.Empty(harness.Terminal.Launches);
        Assert.True(plan.HasError);
    }

    [Fact]
    public async Task Edit_with_Claude_confirms_while_a_run_is_working_and_stops_on_cancel()
    {
        using var harness = WithProject(out _);
        harness.Locator.Resolution =
            new ClaudeExecutableResolution(@"C:\claude.cmd", ClaudeExecutableSource.ConfiguredPath, null, []);

        var plan = harness.CreatePlan();
        await plan.LoadAsync();
        using var run = await harness.StartHeldRunAsync();

        harness.Confirmation.Answer = false;
        await plan.EditWithClaudeCommand.ExecuteAsync(null);

        Assert.Equal(1, harness.Confirmation.CallCount);
        Assert.Empty(harness.Terminal.Launches);
    }

    [Fact]
    public async Task Edit_with_Claude_offers_to_save_an_unsaved_buffer_first()
    {
        using var harness = WithProject(out var planPath);
        harness.Locator.Resolution =
            new ClaudeExecutableResolution(@"C:\claude.cmd", ClaudeExecutableSource.ConfiguredPath, null, []);

        var plan = harness.CreatePlan();
        await plan.LoadAsync();

        plan.BeginEditCommand.Execute(null);
        plan.RawText = "# Mine\n";
        harness.Confirmation.Answer = true;

        await plan.EditWithClaudeCommand.ExecuteAsync(null);

        // Claude reads the file from disk, so it must match what is on screen before it opens.
        Assert.Equal("# Mine\n", File.ReadAllText(planPath).Replace("\r\n", "\n"));
        Assert.Single(harness.Terminal.Launches);
    }

    [Fact]
    public async Task Edit_with_Claude_puts_the_conventions_on_the_clipboard()
    {
        using var harness = WithProject(out _);
        harness.Locator.Resolution =
            new ClaudeExecutableResolution(@"C:\claude.cmd", ClaudeExecutableSource.ConfiguredPath, null, []);

        var plan = harness.CreatePlan();
        await plan.LoadAsync();

        await plan.EditWithClaudeCommand.ExecuteAsync(null);

        // The literal markers go here rather than into the command line, where cmd.exe would eat
        // the exclamation mark.
        Assert.Contains("- [!]", harness.Clipboard.Texts[^1]!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PlanFormat.Checkbox)]
    [InlineData(PlanFormat.Milestone)]
    public void The_seed_prompt_is_free_of_everything_cmd_would_reinterpret(PlanFormat format)
    {
        // This is the test that stops a future rewording silently breaking the launch: a hazardous
        // prompt is dropped by the launcher rather than passed, so the session would open bare.
        var prompt = PlanDocumentViewModel.SeedPromptFor(format, "plan.md");

        Assert.False(ProcessArguments.HasCommandShellHazard(prompt));
        Assert.Contains("plan.md", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_seed_prompt_describes_the_format_the_plan_actually_uses()
    {
        Assert.Contains("milestone plan", PlanDocumentViewModel.SeedPromptFor(PlanFormat.Milestone, "plan.md"),
            StringComparison.Ordinal);
        Assert.Contains("checkbox task list", PlanDocumentViewModel.SeedPromptFor(PlanFormat.Checkbox, "plan.md"),
            StringComparison.Ordinal);
    }
}
