using System.Text;
using System.Text.Json.Nodes;
using NightShift.Core.Execution;

namespace NightShift.Core.Tests.Execution;

/// <summary>
/// Coverage for the NDJSON stream decoder (plan.md §5.4).
/// </summary>
/// <remarks>
/// <para>
/// <c>stream-real-session-v2.1.220.jsonl</c> and <c>stream-real-tool-use-v2.1.220.jsonl</c> are real
/// <c>claude -p --output-format stream-json --verbose --include-partial-messages
/// --permission-mode auto</c> runs of <b>Claude Code 2.1.220</b>, captured 2026-07-26 and sanitised:
/// the Windows user name became <c>testuser</c>, session / uuid / message / tool ids became stable
/// fakes, and the 4 KB SessionStart hook output was truncated. Every key name and every structure is
/// the original — so if these tests start failing after a CLI upgrade, the schema moved and the
/// parser needs a look, which is the entire point of committing the bytes instead of a hand-written
/// approximation.
/// </para>
/// <para>
/// The first capture is a plain text answer; the second writes and reads a file, which is where the
/// <c>input_json_delta</c> / <c>tool_use</c> / <c>tool_result</c> shapes come from. <c>stream-noisy</c>
/// wraps real lines in hand-written npm/shell noise, blank lines, CRLF and broken JSON. Nothing here
/// ever spawns a process.
/// </para>
/// </remarks>
public sealed class StreamJsonParserTests
{
    const string HelloRun = "stream-real-session-v2.1.220.jsonl";
    const string ToolRunFixture = "stream-real-tool-use-v2.1.220.jsonl";
    const string HelloSessionId = "5e551011-0000-4000-8000-000000000001";
    const string ToolSessionId = "5e551011-0000-4000-8000-000000000002";

    static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    static IReadOnlyList<ClaudeStreamEvent> ParseFixture(string name) =>
        StreamJsonParser.ParseAll(Fixture(name));

    static IReadOnlyList<ClaudeStreamEvent> HelloEvents() => ParseFixture(HelloRun);

    static IReadOnlyList<ClaudeStreamEvent> ToolEvents() => ParseFixture(ToolRunFixture);

    static T Single<T>(IEnumerable<ClaudeStreamEvent> events) where T : ClaudeStreamEvent =>
        events.OfType<T>().Single();

    /// <summary>A short code per event, so a 43-line replay reads as one legible sequence.</summary>
    static string Shape(ClaudeStreamEvent e) => e switch
    {
        ClaudeHookStartedEvent => "hookStart",
        ClaudeHookResponseEvent => "hookDone",
        ClaudeInitEvent => "init",
        ClaudeStatusEvent => "status",
        ClaudeContentBlockStartEvent block => "blockStart:" + block.BlockType,
        ClaudeInputJsonDeltaEvent => "argDelta",
        ClaudeTextDeltaEvent => "textDelta",
        ClaudeToolResultEvent => "toolResult",
        ClaudeMessageEvent message => "message:" + message.Role,
        ClaudeRateLimitEvent => "rateLimit",
        ClaudeResultEvent => "result",
        ClaudeUnknownEvent unknown => "?" + unknown.Subtype,
        _ => e.GetType().Name,
    };

    // ── The plain-text session, replayed end to end ────────────────────────────────────────────

    [Fact]
    public void The_plain_text_capture_replays_in_order_with_every_line_accounted_for()
    {
        Assert.Equal(
            [
                "hookStart",
                "hookDone",
                "init",
                "status",
                "?message_start",
                "blockStart:text",
                "textDelta",
                "textDelta",
                "message:Assistant",
                "?content_block_stop",
                "?message_delta",
                "?message_stop",
                "rateLimit",
                "result",
            ],
            HelloEvents().Select(Shape));
    }

    [Fact]
    public void Every_line_of_the_real_captures_carries_its_session_id()
    {
        Assert.All(HelloEvents(), e => Assert.Equal(HelloSessionId, e.SessionId));
        Assert.All(ToolEvents(), e => Assert.Equal(ToolSessionId, e.SessionId));
    }

    [Fact]
    public void Every_event_keeps_the_raw_line_it_came_from()
    {
        Assert.All(HelloEvents(), e => Assert.StartsWith("{", e.RawLine, StringComparison.Ordinal));
        Assert.All(ToolEvents(), e => Assert.StartsWith("{", e.RawLine, StringComparison.Ordinal));
    }

    // ── The tool-use session, replayed end to end ──────────────────────────────────────────────

    [Fact]
    public void The_tool_use_capture_replays_in_order_with_every_line_accounted_for()
    {
        // Write → tool_result → Read → tool_result → a text answer, over three assistant turns.
        Assert.Equal(
            [
                "hookStart", "hookDone", "init", "status",
                "?message_start", "blockStart:tool_use",
                "argDelta", "argDelta", "argDelta", "argDelta", "argDelta", "argDelta", "argDelta",
                "message:Assistant", "?content_block_stop", "toolResult",
                "?message_delta", "?message_stop", "rateLimit",
                "status",
                "?message_start", "blockStart:tool_use",
                "argDelta", "argDelta", "argDelta", "argDelta",
                "message:Assistant", "?content_block_stop", "toolResult",
                "?message_delta", "?message_stop",
                "status",
                "?message_start", "blockStart:text",
                "textDelta", "textDelta", "textDelta", "textDelta",
                "message:Assistant", "?content_block_stop",
                "?message_delta", "?message_stop",
                "result",
            ],
            ToolEvents().Select(Shape));
    }

    [Fact]
    public void A_content_block_start_announces_the_tool_before_its_arguments_stream()
    {
        // The id and name arrive here, ahead of every argument fragment, which is what lets the UI
        // say "running Write…" instead of waiting for the whole call to assemble.
        var blocks = ToolEvents().OfType<ClaudeContentBlockStartEvent>().ToArray();

        Assert.Equal(3, blocks.Length);
        Assert.Equal(["Write", "Read", null], blocks.Select(b => b.ToolName));
        Assert.Equal([true, true, false], blocks.Select(b => b.IsToolUse));
        Assert.Equal("text", blocks[2].BlockType);
        Assert.All(blocks, b => Assert.Equal(0, b.Index));
        Assert.StartsWith("toolu_", blocks[0].ToolUseId, StringComparison.Ordinal);
    }

    [Fact]
    public void Tool_argument_fragments_reassemble_into_the_call_the_assistant_message_reports()
    {
        var events = ToolEvents();
        var fragments = events.OfType<ClaudeInputJsonDeltaEvent>().ToArray();

        // 11 of 43 lines — the most common delta in a working run, and absent from plan.md §5.4.
        Assert.Equal(11, fragments.Length);

        // The first fragment of a call is an empty string; the rest are arbitrary slices that only
        // mean anything concatenated in order.
        Assert.Equal(string.Empty, fragments[0].PartialJson);
        Assert.All(fragments, f => Assert.Equal(0, f.Index));

        var firstCall = string.Concat(fragments.Take(7).Select(f => f.PartialJson));
        var write = Assert.Single(events.OfType<ClaudeMessageEvent>().First().ToolUses);

        Assert.Equal("Write", write.Name);
        Assert.Equal(
            JsonNode.Parse(write.InputJson!)!.ToJsonString(),
            JsonNode.Parse(firstCall)!.ToJsonString());
    }

    [Fact]
    public void Tool_argument_fragments_never_leak_into_the_transcript_text()
    {
        // Appending raw JSON shards to the live pane would look like corruption. The text stream must
        // be exactly the assistant's prose, and nothing else.
        var events = ToolEvents();
        var transcript = string.Concat(events.OfType<ClaudeTextDeltaEvent>().Select(d => d.Text));

        Assert.DoesNotContain("file_path", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("{", transcript, StringComparison.Ordinal);
        Assert.StartsWith("hello.txt written", transcript, StringComparison.Ordinal);
        Assert.Equal(Single<ClaudeResultEvent>(events).ResultText, transcript);
    }

    [Fact]
    public void An_assistant_message_that_only_calls_a_tool_has_no_transcript_text()
    {
        var messages = ToolEvents().OfType<ClaudeMessageEvent>().ToArray();

        Assert.Equal(3, messages.Length);
        Assert.All(messages, m => Assert.Equal(ClaudeMessageRole.Assistant, m.Role));

        Assert.Equal(string.Empty, messages[0].Text);
        Assert.Equal("Write", Assert.Single(messages[0].ToolUses).Name);
        Assert.Equal("Read", Assert.Single(messages[1].ToolUses).Name);

        Assert.Empty(messages[2].ToolUses);
        Assert.StartsWith("hello.txt written", messages[2].Text, StringComparison.Ordinal);
        Assert.Equal("claude-opus-5", messages[2].Model);
    }

    [Fact]
    public void A_user_line_carrying_a_tool_result_is_not_modelled_as_a_user_turn()
    {
        // There is no human in an unattended run, so every `user` line is plumbing. Showing it as a
        // user turn would invent an operator who is not there (plan.md §5.3.3).
        var events = ToolEvents();
        var results = events.OfType<ClaudeToolResultEvent>().ToArray();

        Assert.Equal(2, results.Length);
        Assert.DoesNotContain(events.OfType<ClaudeMessageEvent>(), m => m.Role == ClaudeMessageRole.User);

        Assert.StartsWith("File created successfully at:", results[0].Text, StringComparison.Ordinal);
        Assert.Equal("1\thi", results[1].Text);
        Assert.All(results, r => Assert.False(r.IsError));
    }

    [Fact]
    public void A_tool_result_links_back_to_the_call_it_answers()
    {
        var events = ToolEvents();
        var calls = events.OfType<ClaudeMessageEvent>().SelectMany(m => m.ToolUses).ToArray();
        var results = events.OfType<ClaudeToolResultEvent>().ToArray();

        Assert.Equal(calls[0].Id, results[0].ToolUseId);
        Assert.Equal(calls[1].Id, results[1].ToolUseId);
        Assert.NotEqual(results[0].ToolUseId, results[1].ToolUseId);
    }

    [Fact]
    public void A_failing_tool_result_sets_the_error_flag()
    {
        var result = Assert.IsType<ClaudeToolResultEvent>(StreamJsonParser.ParseLine(
            """{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"File does not exist.","is_error":true}]},"session_id":"s"}"""));

        Assert.True(result.IsError);
        Assert.Equal("toolu_1", result.ToolUseId);
        Assert.Equal("File does not exist.", result.Text);
    }

    [Fact]
    public void A_user_line_with_no_tool_result_is_still_a_user_message()
    {
        var message = Assert.IsType<ClaudeMessageEvent>(StreamJsonParser.ParseLine(
            """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"the prompt"}]},"session_id":"s"}"""));

        Assert.Equal(ClaudeMessageRole.User, message.Role);
        Assert.Equal("the prompt", message.Text);
    }

    [Fact]
    public void The_tool_run_result_reports_three_turns_and_its_own_cost()
    {
        var result = Single<ClaudeResultEvent>(ToolEvents());

        Assert.Equal(3, result.NumTurns);
        Assert.Equal(0.120045d, result.TotalCostUsd!.Value, precision: 9);
        Assert.Equal(TimeSpan.FromMilliseconds(9467), result.Duration);
        Assert.False(result.IsError);
    }

    // ── system / init ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_init_line_captures_the_session_identity()
    {
        var init = Single<ClaudeInitEvent>(HelloEvents());

        Assert.Equal(HelloSessionId, init.SessionId);
        Assert.Equal("claude-opus-5[1m]", init.Model);
        Assert.Equal("2.1.220", init.ClaudeCodeVersion);
        Assert.EndsWith("streamcap", init.Cwd, StringComparison.Ordinal);
        Assert.Equal("aaaaaaaa-0000-4000-8000-000000000006", init.Uuid);
    }

    [Fact]
    public void The_init_line_reports_the_permission_mode_the_run_actually_got()
    {
        // Measured, not assumed: this is what RunRecord.PermissionModeUsed should record when the
        // §5.3.2 fallback ladder downgrades a run (plan.md §11.4c).
        Assert.Equal("auto", Single<ClaudeInitEvent>(HelloEvents()).PermissionMode);
        Assert.Equal("auto", Single<ClaudeInitEvent>(ToolEvents()).PermissionMode);
    }

    [Fact]
    public void The_init_line_proves_AskUserQuestion_was_stripped_from_the_tool_list()
    {
        // plan.md §5.3.3 passes --disallowedTools "AskUserQuestion" so nothing can pause the run to
        // poll a developer who is not there. The init line is where that becomes observable.
        var init = Single<ClaudeInitEvent>(HelloEvents());

        Assert.NotEmpty(init.Tools);
        Assert.Contains("Bash", init.Tools);
        Assert.DoesNotContain("AskUserQuestion", init.Tools);
    }

    [Fact]
    public void A_healthy_run_reports_caveman_loaded_and_no_plugin_errors()
    {
        var init = Single<ClaudeInitEvent>(HelloEvents());

        // The healthy capture omits `plugin_errors` entirely rather than sending an empty array —
        // absence must read as good news, not as an unparseable payload.
        Assert.Empty(init.PluginErrors);
        Assert.False(init.HasCavemanPluginError);
        Assert.True(init.CavemanPluginLoaded);
        Assert.False(init.CavemanUnavailable);
    }

    [Fact]
    public void A_plugin_without_a_version_field_is_still_a_loaded_plugin()
    {
        // The real caveman entry is { name, path, source } with no `version`, unlike the llm-wiki
        // entry beside it. Requiring `version` would report the plugin as missing.
        var init = Single<ClaudeInitEvent>(HelloEvents());

        Assert.Contains("caveman", init.Plugins);
        Assert.Contains("llm-wiki", init.Plugins);
        Assert.DoesNotContain(init.Plugins, name => name.Contains('{', StringComparison.Ordinal));
    }

    [Fact]
    public void Caveman_is_also_detected_through_the_slash_commands_and_skills()
    {
        var init = Single<ClaudeInitEvent>(HelloEvents());

        Assert.Contains("caveman:caveman-commit", init.SlashCommands);
        Assert.Contains("caveman:caveman", init.Skills);
    }

    [Fact]
    public void Plugin_errors_mentioning_caveman_are_surfaced_distinctly()
    {
        var init = Single<ClaudeInitEvent>(ParseFixture("stream-init-caveman-failed.jsonl"));

        Assert.Equal(2, init.PluginErrors.Count);
        var cavemanError = Assert.Single(init.CavemanPluginErrors);
        Assert.Contains("caveman@caveman", cavemanError, StringComparison.Ordinal);

        // The llm-wiki failure is kept but must not masquerade as the caveman one.
        Assert.DoesNotContain(init.CavemanPluginErrors, e => e.Contains("llm-wiki", StringComparison.Ordinal));

        Assert.True(init.HasCavemanPluginError);
        Assert.False(init.CavemanPluginLoaded);
        Assert.True(init.CavemanUnavailable);
    }

    [Fact]
    public void Caveman_missing_from_the_inventory_is_flagged_even_without_a_plugin_error()
    {
        var init = Assert.IsType<ClaudeInitEvent>(StreamJsonParser.ParseLine(
            """{"type":"system","subtype":"init","session_id":"s","plugins":[{"name":"llm-wiki"}],"slash_commands":["init"],"skills":["dataviz"]}"""));

        Assert.False(init.HasCavemanPluginError);
        Assert.False(init.CavemanPluginLoaded);
        Assert.True(init.CavemanUnavailable);
    }

    [Fact]
    public void An_init_line_with_no_inventory_at_all_does_not_claim_caveman_is_missing()
    {
        // No plugins, no skills, no slash commands: the CLI told us nothing, so we must not invent a
        // warning. Only positive evidence counts.
        var init = Assert.IsType<ClaudeInitEvent>(StreamJsonParser.ParseLine(
            """{"type":"system","subtype":"init","session_id":"s","model":"m"}"""));

        Assert.False(init.CavemanUnavailable);
    }

    // ── system / hook_started, hook_response, status ───────────────────────────────────────────

    [Fact]
    public void Hook_start_and_hook_response_are_typed_and_pair_up_by_id()
    {
        var events = HelloEvents();
        var started = Single<ClaudeHookStartedEvent>(events);
        var response = Single<ClaudeHookResponseEvent>(events);

        Assert.Equal("SessionStart:startup", started.HookName);
        Assert.Equal("SessionStart", started.HookEvent);
        Assert.Equal(started.HookId, response.HookId);
        Assert.Equal("success", response.Outcome);
        Assert.Equal(0, response.ExitCode);
        Assert.Equal(string.Empty, response.Stderr);
        Assert.False(response.Failed);
    }

    [Fact]
    public void A_failing_hook_is_reported_as_failed()
    {
        var response = Assert.IsType<ClaudeHookResponseEvent>(StreamJsonParser.ParseLine(
            """{"type":"system","subtype":"hook_response","hook_id":"h","hook_name":"PreToolUse","exit_code":2,"outcome":"error","stderr":"boom"}"""));

        Assert.True(response.Failed);
        Assert.Equal("boom", response.Stderr);
    }

    [Fact]
    public void A_json_string_containing_an_escaped_crlf_does_not_split_the_line()
    {
        // The hook output embeds "\r\n" inside a JSON string. Framing must be driven by real
        // newlines in the byte stream, never by escaped ones inside a value.
        var response = Single<ClaudeHookResponseEvent>(HelloEvents());

        Assert.NotNull(response.Output);
        Assert.Contains("\r\n", response.Output, StringComparison.Ordinal);
        Assert.Equal(response.Output, response.Stdout);
    }

    [Fact]
    public void Status_lines_are_typed()
    {
        Assert.Equal("requesting", Single<ClaudeStatusEvent>(HelloEvents()).Status);
        Assert.Equal(3, ToolEvents().OfType<ClaudeStatusEvent>().Count());
    }

    // ── rate_limit_event: the 100x trap ────────────────────────────────────────────────────────

    [Fact]
    public void The_rate_limit_event_converts_utilization_from_a_fraction_to_a_percentage()
    {
        var rateLimit = Single<ClaudeRateLimitEvent>(HelloEvents());

        // 0.36 on the wire is 36%, not 0.36%. The OAuth endpoint reported 37% for the same seven_day
        // window minutes later. Getting this wrong by 100x either burns a night of quota or never
        // runs at all (plan.md §4.3).
        Assert.Equal(0.36d, rateLimit.UtilizationFraction!.Value, precision: 6);
        Assert.Equal(36d, rateLimit.UtilizationPercent!.Value, precision: 6);
        Assert.Equal("seven_day", rateLimit.RateLimitType);
        Assert.Equal("allowed_warning", rateLimit.Status);
        Assert.False(rateLimit.IsUsingOverage);
    }

    [Fact]
    public void The_rate_limit_reset_is_read_as_epoch_seconds_not_milliseconds()
    {
        var rateLimit = Single<ClaudeRateLimitEvent>(HelloEvents());

        // 1785621600 seconds. Read as milliseconds it would land in 1970 and the scheduler would
        // resume immediately instead of when the window rolls over (plan.md §6).
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 22, 0, 0, TimeSpan.Zero), rateLimit.ResetsAt);
    }

    [Fact]
    public void A_five_hour_rate_limit_event_is_read_the_same_way()
    {
        var rateLimit = Assert.IsType<ClaudeRateLimitEvent>(StreamJsonParser.ParseLine(
            """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1785621600,"rateLimitType":"five_hour","utilization":0.07,"isUsingOverage":true},"session_id":"s"}"""));

        Assert.Equal("five_hour", rateLimit.RateLimitType);
        Assert.Equal(7d, rateLimit.UtilizationPercent!.Value, precision: 6);
        Assert.True(rateLimit.IsUsingOverage);
    }

    [Fact]
    public void A_rate_limit_utilization_beyond_the_range_is_clamped_to_a_hundred()
    {
        var rateLimit = Assert.IsType<ClaudeRateLimitEvent>(StreamJsonParser.ParseLine(
            """{"type":"rate_limit_event","rate_limit_info":{"utilization":1.4}}"""));

        Assert.Equal(100d, rateLimit.UtilizationPercent!.Value, precision: 6);
        Assert.Equal(1.4d, rateLimit.UtilizationFraction!.Value, precision: 6);
    }

    [Fact]
    public void A_flattened_rate_limit_payload_and_an_iso_reset_are_still_read()
    {
        var rateLimit = Assert.IsType<ClaudeRateLimitEvent>(StreamJsonParser.ParseLine(
            """{"type":"rate_limit_event","utilization":0.1,"resetsAt":"2026-08-01T22:00:00Z"}"""));

        Assert.Equal(10d, rateLimit.UtilizationPercent!.Value, precision: 6);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 22, 0, 0, TimeSpan.Zero), rateLimit.ResetsAt);
    }

    // ── Messages and deltas ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Text_deltas_carry_the_incremental_text()
    {
        var deltas = HelloEvents().OfType<ClaudeTextDeltaEvent>().Select(d => d.Text).ToArray();

        Assert.Equal(["h", "ello from nightshift"], deltas);
        Assert.Equal("hello from nightshift", string.Concat(deltas));
    }

    [Fact]
    public void The_assistant_message_carries_the_flattened_text_and_the_model()
    {
        var message = Single<ClaudeMessageEvent>(HelloEvents());

        Assert.Equal(ClaudeMessageRole.Assistant, message.Role);
        Assert.Equal("hello from nightshift", message.Text);
        Assert.Equal("claude-opus-5", message.Model);
        Assert.Empty(message.ToolUses);
    }

    [Fact]
    public void Text_extraction_walks_a_mixed_content_array_and_ignores_the_rest()
    {
        var message = Assert.IsType<ClaudeMessageEvent>(StreamJsonParser.ParseLine(
            """{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"secret"},{"type":"text","text":"visible"},{"type":"tool_use","id":"t","name":"Bash","input":{"command":"ls"}}]}}"""));

        Assert.Equal("visible", message.Text);
        var use = Assert.Single(message.ToolUses);
        Assert.Equal("Bash", use.Name);
        Assert.Contains("\"command\":\"ls\"", use.InputJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Partial_message_stream_events_become_unknown_events_tagged_with_their_inner_type()
    {
        var unknown = HelloEvents().OfType<ClaudeUnknownEvent>().ToArray();

        Assert.All(unknown, e => Assert.Equal("stream_event", e.Type));
        Assert.Equal(
            ["message_start", "content_block_stop", "message_delta", "message_stop"],
            unknown.Select(e => e.Subtype));
    }

    // ── result ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_result_line_captures_everything_a_run_record_needs()
    {
        var result = Single<ClaudeResultEvent>(HelloEvents());

        Assert.False(result.IsError);
        Assert.Equal("success", result.Subtype);
        Assert.Equal(HelloSessionId, result.SessionId);
        Assert.Equal(0.105111d, result.TotalCostUsd!.Value, precision: 9);
        Assert.Equal(TimeSpan.FromMilliseconds(3754), result.Duration);
        Assert.Equal(TimeSpan.FromMilliseconds(3602), result.ApiDuration);
        Assert.Equal("hello from nightshift", result.ResultText);
        Assert.Equal("end_turn", result.StopReason);
        Assert.Equal("completed", result.TerminalReason);
        Assert.Null(result.ApiErrorStatus);
        Assert.Equal(1, result.NumTurns);
        Assert.Empty(result.PermissionDenials);
    }

    [Fact]
    public void The_result_line_is_recognised_even_though_type_is_not_its_first_key()
    {
        // The real result object opens with `is_error` and puts `type` near the end; nothing may
        // assume key ordering.
        var raw = Fixture(HelloRun).Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1];

        Assert.StartsWith("{\"is_error\"", raw, StringComparison.Ordinal);
        Assert.IsType<ClaudeResultEvent>(StreamJsonParser.ParseLine(raw));
    }

    [Fact]
    public void A_failed_result_reports_the_error_flag_and_the_denials()
    {
        var result = Assert.IsType<ClaudeResultEvent>(StreamJsonParser.ParseLine(
            """{"type":"result","subtype":"error_max_turns","is_error":true,"duration_ms":1000,"session_id":"s","permission_denials":[{"tool_name":"Bash","tool_input":{"command":"rm -rf /"}}]}"""));

        Assert.True(result.IsError);
        Assert.Equal("error_max_turns", result.Subtype);
        Assert.Contains("Bash", Assert.Single(result.PermissionDenials), StringComparison.Ordinal);
    }

    // ── system / api_retry (specified by plan.md, absent from both captures) ───────────────────

    [Fact]
    public void An_api_retry_line_reports_the_attempt_number()
    {
        var retry = Assert.IsType<ClaudeApiRetryEvent>(StreamJsonParser.ParseLine(
            """{"type":"system","subtype":"api_retry","attempt":3,"delayMs":2000,"error":"Overloaded","session_id":"s"}"""));

        Assert.Equal(3, retry.Attempt);
        Assert.Equal(TimeSpan.FromSeconds(2), retry.Delay);
        Assert.Equal("Overloaded", retry.Message);
        Assert.Equal("s", retry.SessionId);
    }

    [Fact]
    public void An_api_retry_line_tolerates_the_other_plausible_field_names()
    {
        // Unobserved shape, so the reader accepts the spellings the CLI has used elsewhere rather
        // than betting on one.
        var retry = Assert.IsType<ClaudeApiRetryEvent>(StreamJsonParser.ParseLine(
            """{"type":"system","subtype":"api_retry","attemptNumber":"2","delay_ms":500,"message":"429"}"""));

        Assert.Equal(2, retry.Attempt);
        Assert.Equal(TimeSpan.FromMilliseconds(500), retry.Delay);
        Assert.Equal("429", retry.Message);
    }

    [Fact]
    public void An_api_retry_line_with_no_attempt_number_is_still_an_api_retry()
    {
        var retry = Assert.IsType<ClaudeApiRetryEvent>(StreamJsonParser.ParseLine(
            """{"type":"system","subtype":"api_retry"}"""));

        Assert.Null(retry.Attempt);
    }

    // ── Framing: chunks, line endings, blanks ──────────────────────────────────────────────────

    [Fact]
    public void A_line_split_across_three_feed_calls_produces_one_event_when_the_newline_arrives()
    {
        var parser = new StreamJsonParser();

        Assert.Empty(parser.Feed("""{"type":"system","subtype":"in"""));
        Assert.Empty(parser.Feed("""it","session_id":"s","mod"""));
        var events = parser.Feed("""el":"claude-opus-5"}""" + "\n");

        var init = Assert.IsType<ClaudeInitEvent>(Assert.Single(events));
        Assert.Equal("claude-opus-5", init.Model);
        Assert.Equal("s", init.SessionId);
        Assert.Equal(0, parser.BufferedLength);
    }

    [Fact]
    public void Splitting_a_whole_real_capture_one_character_at_a_time_changes_nothing()
    {
        var parser = new StreamJsonParser();
        var events = new List<ClaudeStreamEvent>();

        foreach (var c in Fixture(ToolRunFixture))
        {
            events.AddRange(parser.Feed(c.ToString()));
        }

        events.AddRange(parser.Flush());

        Assert.Equal(ToolEvents().Select(Shape), events.Select(Shape));
        Assert.Equal(ToolEvents().Select(e => e.RawLine), events.Select(e => e.RawLine));
    }

    [Fact]
    public void Several_json_objects_arriving_in_one_chunk_are_all_parsed_in_order()
    {
        var chunk = string.Join('\n',
            """{"type":"system","subtype":"status","status":"requesting"}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"a"}}}""",
            """{"type":"result","is_error":false,"duration_ms":1}""") + "\n";

        var events = new StreamJsonParser().Feed(chunk);

        Assert.Equal(3, events.Count);
        Assert.IsType<ClaudeStatusEvent>(events[0]);
        Assert.IsType<ClaudeTextDeltaEvent>(events[1]);
        Assert.IsType<ClaudeResultEvent>(events[2]);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Both_line_endings_are_handled(string newline)
    {
        var text = string.Join(newline,
            """{"type":"system","subtype":"status","status":"requesting"}""",
            """{"type":"result","is_error":false}""") + newline;

        var events = StreamJsonParser.ParseAll(text);

        Assert.Equal(2, events.Count);
        Assert.IsType<ClaudeStatusEvent>(events[0]);
        Assert.IsType<ClaudeResultEvent>(events[1]);

        // The carriage return must not survive into the raw line either.
        Assert.All(events, e => Assert.DoesNotContain("\r", e.RawLine, StringComparison.Ordinal));
    }

    [Fact]
    public void A_chunk_boundary_between_the_carriage_return_and_the_line_feed_is_handled()
    {
        var parser = new StreamJsonParser();

        Assert.Empty(parser.Feed("""{"type":"system","subtype":"status","status":"requesting"}""" + "\r"));
        var events = parser.Feed("\n");

        var status = Assert.IsType<ClaudeStatusEvent>(Assert.Single(events));
        Assert.Equal("requesting", status.Status);
        Assert.DoesNotContain("\r", status.RawLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Blank_and_whitespace_only_lines_produce_no_events()
    {
        var events = StreamJsonParser.ParseAll(
            "\n\r\n   \n\t\n" + """{"type":"result","is_error":false}""" + "\n\n\n");

        Assert.IsType<ClaudeResultEvent>(Assert.Single(events));
    }

    [Fact]
    public void An_empty_stream_produces_no_events()
    {
        Assert.Empty(StreamJsonParser.ParseAll(string.Empty));
        Assert.Empty(StreamJsonParser.ParseAll((string?)null));
        Assert.Empty(StreamJsonParser.ParseAll(new StringReader(string.Empty)));
        Assert.Empty(new StreamJsonParser().Flush());
    }

    [Fact]
    public void Flush_emits_a_final_line_that_never_got_its_newline()
    {
        // A killed process (MaxRunDurationMinutes, stall detector — plan.md §5.4) can cut the stream
        // mid-line, and the truncated line is often the one that matters.
        var parser = new StreamJsonParser();

        Assert.Empty(parser.Feed("""{"type":"result","is_error":false,"total_cost_usd":0.5}"""));

        var result = Assert.IsType<ClaudeResultEvent>(Assert.Single(parser.Flush()));
        Assert.Equal(0.5d, result.TotalCostUsd!.Value, precision: 6);
        Assert.Empty(parser.Flush());
    }

    [Fact]
    public void Reset_drops_a_partial_line()
    {
        var parser = new StreamJsonParser();
        parser.Feed("""{"type":"result",""");

        Assert.True(parser.BufferedLength > 0);
        parser.Reset();

        Assert.Equal(0, parser.BufferedLength);
        Assert.Empty(parser.Flush());
    }

    [Fact]
    public void Feeding_from_a_char_buffer_matches_feeding_a_string()
    {
        var buffer = Fixture(HelloRun).ToCharArray();
        var parser = new StreamJsonParser();

        var events = new List<ClaudeStreamEvent>(parser.Feed(buffer, 0, buffer.Length));
        events.AddRange(parser.Flush());

        Assert.Equal(HelloEvents().Select(e => e.RawLine), events.Select(e => e.RawLine));
        Assert.Empty(new StreamJsonParser().Feed(buffer, 0, 0));
    }

    // ── Noise and malformed input: nothing may throw ───────────────────────────────────────────

    [Fact]
    public void Npm_and_shell_noise_is_reported_as_non_json_and_never_stops_the_parse()
    {
        var events = ParseFixture("stream-noisy.jsonl");

        Assert.Equal(
            [
                typeof(ClaudeNonJsonLineEvent),   // npm WARN exec ...
                typeof(ClaudeNonJsonLineEvent),   // Need to install the following packages:
                typeof(ClaudeNonJsonLineEvent),   // claude@2.1.220
                typeof(ClaudeNonJsonLineEvent),   // Ok to proceed? (y)
                typeof(ClaudeInitEvent),
                typeof(ClaudeMalformedLineEvent), // a truncated assistant message
                typeof(ClaudeTextDeltaEvent),     // recovery: the very next line parses
                typeof(ClaudeUnknownEvent),       // an event type we do not model
                typeof(ClaudeResultEvent),
                typeof(ClaudeNonJsonLineEvent),   // done in 4.2s
            ],
            events.Select(e => e.GetType()));

        Assert.StartsWith("npm WARN", events[0].RawLine, StringComparison.Ordinal);
        Assert.Equal("ello from nightshift", ((ClaudeTextDeltaEvent)events[6]).Text);
    }

    [Fact]
    public void Malformed_json_becomes_a_malformed_line_event_carrying_the_raw_line()
    {
        var malformed = Assert.IsType<ClaudeMalformedLineEvent>(
            StreamJsonParser.ParseLine("""{"type":"assistant","message":{"content":[{"text":"half a li"""));

        Assert.NotEmpty(malformed.Error);
        Assert.StartsWith("{\"type\":\"assistant\"", malformed.RawLine, StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_that_is_not_an_object_is_treated_as_noise_rather_than_broken_json()
    {
        // NDJSON here is one object per line, so a bare array or scalar is shim output, not a schema
        // change. Calling it "malformed" would cry wolf on every npm banner.
        Assert.IsType<ClaudeNonJsonLineEvent>(StreamJsonParser.ParseLine("[1,2,3]"));
        Assert.IsType<ClaudeNonJsonLineEvent>(StreamJsonParser.ParseLine("null"));
        Assert.IsType<ClaudeNonJsonLineEvent>(StreamJsonParser.ParseLine("42"));
    }

    [Fact]
    public void An_unknown_event_type_is_generic_and_keeps_the_raw_line()
    {
        const string line = """{"type":"telemetry_v9","subtype":"flush","payload":{"unrecognised":true},"session_id":"s"}""";

        var unknown = Assert.IsType<ClaudeUnknownEvent>(StreamJsonParser.ParseLine(line));

        Assert.Equal("telemetry_v9", unknown.Type);
        Assert.Equal("flush", unknown.Subtype);
        Assert.Equal(line, unknown.RawLine);
        Assert.Equal("s", unknown.SessionId);
    }

    [Fact]
    public void An_unmodelled_system_subtype_stays_a_system_unknown_event()
    {
        var unknown = Assert.IsType<ClaudeUnknownEvent>(StreamJsonParser.ParseLine(
            """{"type":"system","subtype":"compact_boundary","session_id":"s"}"""));

        Assert.Equal("system", unknown.Type);
        Assert.Equal("compact_boundary", unknown.Subtype);
    }

    [Fact]
    public void A_line_with_no_type_at_all_is_unknown_rather_than_malformed()
    {
        var unknown = Assert.IsType<ClaudeUnknownEvent>(StreamJsonParser.ParseLine("""{"hello":"world"}"""));

        Assert.Null(unknown.Type);
    }

    [Fact]
    public void ParseLine_returns_null_only_for_a_blank_line()
    {
        Assert.Null(StreamJsonParser.ParseLine(string.Empty));
        Assert.Null(StreamJsonParser.ParseLine("   \t "));
        Assert.Null(StreamJsonParser.ParseLine(null));
        Assert.NotNull(StreamJsonParser.ParseLine("x"));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"type\":")]
    [InlineData("{}")]
    [InlineData("{ }")]
    [InlineData("{\"type\":123}")]
    [InlineData("{\"type\":{\"nested\":true}}")]
    [InlineData("{\"type\":\"result\"}")]
    [InlineData("{\"type\":\"result\",\"duration_ms\":\"NaN\"}")]
    [InlineData("{\"type\":\"result\",\"duration_ms\":1e400}")]
    [InlineData("{\"type\":\"result\",\"duration_ms\":-5}")]
    [InlineData("{\"type\":\"result\",\"is_error\":\"maybe\",\"total_cost_usd\":\"1.5\"}")]
    [InlineData("{\"type\":\"stream_event\"}")]
    [InlineData("{\"type\":\"stream_event\",\"event\":\"not an object\"}")]
    [InlineData("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_start\"}}")]
    [InlineData("{\"type\":\"stream_event\",\"event\":{\"delta\":{\"type\":\"input_json_delta\"}}}")]
    [InlineData("{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"utilization\":\"abc\",\"resetsAt\":99999999999999999}}")]
    [InlineData("{\"type\":\"system\",\"subtype\":\"init\",\"plugins\":\"caveman\"}")]
    [InlineData("{\"type\":\"system\",\"subtype\":\"init\",\"plugins\":{\"caveman\":\"failed to load\"},\"plugin_errors\":42}")]
    [InlineData("{\"type\":\"system\",\"subtype\":\"init\",\"tools\":[null,{\"no\":\"name\"},7]}")]
    [InlineData("{\"type\":\"assistant\",\"message\":{\"content\":\"a plain string\"}}")]
    [InlineData("{\"type\":\"assistant\",\"message\":{\"content\":[[[[[[[[[[[[\"deep\"]]]]]]]]]]]]}}")]
    [InlineData("{\"type\":\"user\",\"message\":null}")]
    [InlineData("{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\"}]}}")]
    [InlineData("  binary garbage")]
    public void No_line_can_make_the_parser_throw(string line)
    {
        // The whole contract of this class: a third-party format change must degrade the transcript,
        // never kill the reader loop and orphan a claude process mid-run.
        Assert.NotNull(StreamJsonParser.ParseLine(line));
        Assert.Single(StreamJsonParser.ParseAll(line + "\n"));
    }

    [Fact]
    public void A_scalar_where_a_list_was_expected_is_still_read()
    {
        var init = Assert.IsType<ClaudeInitEvent>(StreamJsonParser.ParseLine(
            """{"type":"system","subtype":"init","plugins":"caveman"}"""));

        Assert.Equal(["caveman"], init.Plugins);
        Assert.True(init.CavemanPluginLoaded);
    }

    [Fact]
    public void An_object_map_of_plugin_errors_keeps_its_keys()
    {
        var init = Assert.IsType<ClaudeInitEvent>(StreamJsonParser.ParseLine(
            """{"type":"system","subtype":"init","plugin_errors":{"caveman":"failed to load"}}"""));

        Assert.Equal(["caveman: failed to load"], init.PluginErrors);
        Assert.True(init.HasCavemanPluginError);
    }

    // ── The buffer guard ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_line_that_never_terminates_trips_the_buffer_guard_exactly_once()
    {
        var parser = new StreamJsonParser(maxLineLength: 64);

        var events = parser.Feed(new string('x', 200_000));

        var oversized = Assert.IsType<ClaudeOversizedLineEvent>(Assert.Single(events));
        Assert.Equal(64, oversized.Limit);
        Assert.Equal(64, oversized.RawLine.Length);

        // Still nothing more, however much follows: the rest is discarded, not buffered.
        Assert.Empty(parser.Feed(new string('x', 200_000)));
        Assert.Equal(0, parser.BufferedLength);
    }

    [Fact]
    public void The_oversized_preview_is_capped_even_when_the_limit_is_large()
    {
        var parser = new StreamJsonParser(maxLineLength: 4096);

        var oversized = Assert.IsType<ClaudeOversizedLineEvent>(
            Assert.Single(parser.Feed(new string('y', 10_000))));

        Assert.Equal(StreamJsonParser.OversizedPreviewLength, oversized.RawLine.Length);
    }

    [Fact]
    public void Parsing_recovers_on_the_very_next_line_after_the_guard_trips()
    {
        var parser = new StreamJsonParser(maxLineLength: 256);
        parser.Feed(new string('x', 5_000));

        var events = parser.Feed("\n" + """{"type":"result","is_error":false}""" + "\n");

        Assert.IsType<ClaudeResultEvent>(Assert.Single(events));
    }

    [Fact]
    public void Flush_after_a_tripped_guard_emits_nothing_and_leaves_the_parser_usable()
    {
        var parser = new StreamJsonParser(maxLineLength: 256);
        parser.Feed(new string('x', 5_000));

        Assert.Empty(parser.Flush());
        Assert.Equal(0, parser.BufferedLength);

        Assert.IsType<ClaudeResultEvent>(
            Assert.Single(parser.Feed("""{"type":"result","is_error":true}""" + "\n")));
    }

    [Fact]
    public void A_line_of_exactly_the_limit_is_parsed_and_one_character_more_is_not()
    {
        const string line = """{"type":"result","is_error":false}""";

        Assert.IsType<ClaudeResultEvent>(
            Assert.Single(StreamJsonParser.ParseAll(line + "\n", maxLineLength: line.Length)));

        Assert.IsType<ClaudeOversizedLineEvent>(
            Assert.Single(StreamJsonParser.ParseAll(line + "\n", maxLineLength: line.Length - 1)));
    }

    [Fact]
    public void The_default_limit_comfortably_clears_the_real_captures()
    {
        // The real hook_response line is 8 KB before truncation, and a large tool result is far
        // bigger. The guard must be a backstop, not a routine occurrence.
        Assert.True(StreamJsonParser.DefaultMaxLineLength > 1_000_000);
        Assert.DoesNotContain(HelloEvents(), e => e is ClaudeOversizedLineEvent);
        Assert.DoesNotContain(ToolEvents(), e => e is ClaudeOversizedLineEvent);
    }

    [Fact]
    public void A_nonsensical_buffer_limit_is_a_caller_error()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StreamJsonParser(maxLineLength: 0));
    }

    // ── Reader conveniences ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Parsing_from_a_text_reader_matches_parsing_from_a_string()
    {
        var text = Fixture(ToolRunFixture);

        using var reader = new StringReader(text);
        var fromReader = StreamJsonParser.ParseAll(reader);

        Assert.Equal(
            StreamJsonParser.ParseAll(text).Select(e => e.RawLine),
            fromReader.Select(e => e.RawLine));
    }

    [Fact]
    public async Task Parsing_a_reader_asynchronously_streams_the_same_events()
    {
        using var reader = new StringReader(Fixture(ToolRunFixture));

        var events = new List<ClaudeStreamEvent>();
        await foreach (var e in StreamJsonParser.ParseAsync(reader))
        {
            events.Add(e);
        }

        Assert.Equal(ToolEvents().Select(Shape), events.Select(Shape));
    }

    [Fact]
    public async Task An_unterminated_final_line_is_flushed_by_the_async_reader_too()
    {
        using var reader = new StringReader("""{"type":"result","is_error":false,"num_turns":7}""");

        var events = new List<ClaudeStreamEvent>();
        await foreach (var e in StreamJsonParser.ParseAsync(reader))
        {
            events.Add(e);
        }

        Assert.Equal(7, Assert.IsType<ClaudeResultEvent>(Assert.Single(events)).NumTurns);
    }

    [Fact]
    public async Task The_async_reader_survives_chunk_boundaries_in_the_middle_of_lines()
    {
        // A reader that hands back three characters at a time, as a redirected pipe genuinely can.
        using var reader = new DribblingReader(Fixture(ToolRunFixture), chunkSize: 3);

        var events = new List<ClaudeStreamEvent>();
        await foreach (var e in StreamJsonParser.ParseAsync(reader))
        {
            events.Add(e);
        }

        Assert.Equal(ToolEvents().Select(e => e.RawLine), events.Select(e => e.RawLine));
    }

    /// <summary>A reader that returns at most a few characters per read, like a real pipe.</summary>
    sealed class DribblingReader(string text, int chunkSize) : TextReader
    {
        readonly StringBuilder _remaining = new(text);

        public override int Read(char[] buffer, int index, int count)
        {
            var take = Math.Min(Math.Min(chunkSize, count), _remaining.Length);
            if (take <= 0)
            {
                return 0;
            }

            _remaining.CopyTo(0, buffer, index, take);
            _remaining.Remove(0, take);
            return take;
        }
    }
}
