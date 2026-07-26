namespace NightShift.Core.Execution;

/// <summary>Which side of the conversation a transcript message came from (plan.md §5.4).</summary>
public enum ClaudeMessageRole
{
    Assistant,
    User,
}

/// <summary>
/// One decoded line of Claude Code's <c>--output-format stream-json</c> stdout (plan.md §5.4).
/// </summary>
/// <remarks>
/// <para>
/// The hierarchy is deliberately total: <em>every</em> non-blank line becomes one of these, including
/// lines that are not JSON (<see cref="ClaudeNonJsonLineEvent"/>), lines that are broken JSON
/// (<see cref="ClaudeMalformedLineEvent"/>) and lines whose <c>type</c> we do not model
/// (<see cref="ClaudeUnknownEvent"/>). <see cref="StreamJsonParser"/> never throws for stream
/// content, so a third-party format change degrades an unattended run to "less detail in the
/// transcript" rather than a crash.
/// </para>
/// <para>
/// Every shape here was taken from a real <c>claude -p --output-format stream-json --verbose
/// --include-partial-messages --permission-mode auto</c> run of <b>Claude Code 2.1.220</b>, captured
/// 2026-07-26; the sanitised bytes are the <c>stream-*.jsonl</c> fixtures in the test project. Where
/// a field is <em>not</em> in that capture it is marked as tolerated-but-unobserved on the member.
/// </para>
/// <para>
/// <see cref="RawLine"/> is always the line exactly as it arrived (minus its line terminator), so a
/// caller can log or replay anything it did not understand.
/// </para>
/// <para>
/// These types are hand-built from a <c>JsonNode</c> walk and are never deserialized into, so the
/// source-generator trap documented on <see cref="Configuration.PilotSettings"/> does not apply
/// here. Note that the list-valued properties make record value equality reference-based; compare
/// the contents, not the events.
/// </para>
/// </remarks>
public abstract record ClaudeStreamEvent(string RawLine)
{
    /// <summary>
    /// The <c>session_id</c> the line carried. Present on every line of the reference capture; null
    /// for noise, malformed lines, and any future shape that omits it.
    /// </summary>
    /// <remarks>
    /// This and the two properties below are the envelope every line shares, so the parser stamps
    /// them in one place after construction — hence <c>set</c> rather than <c>init</c> (C# has no
    /// <c>with</c> expression over a type parameter). Treat instances as immutable, exactly as
    /// <see cref="History.RunRecord"/> asks.
    /// </remarks>
    public string? SessionId { get; set; }

    /// <summary>
    /// <c>parent_tool_use_id</c> — non-null when the line came from a subagent rather than the main
    /// turn, which is what lets the transcript pane separate the two.
    /// </summary>
    public string? ParentToolUseId { get; set; }

    /// <summary>The line's own <c>uuid</c>. Useful for de-duplicating a resumed or replayed stream.</summary>
    public string? Uuid { get; set; }
}

/// <summary>
/// The <c>system</c>/<c>init</c> line. Carries the session identity, the permission mode the run
/// <em>actually</em> got, and the plugin inventory that says whether caveman loaded.
/// </summary>
public sealed record ClaudeInitEvent(string RawLine) : ClaudeStreamEvent(RawLine)
{
    /// <summary>Substring used to spot caveman in the plugin/skill/command inventory.</summary>
    public const string CavemanName = "caveman";

    /// <summary>The working directory Claude Code resolved — verify it is the project directory.</summary>
    public string? Cwd { get; init; }

    /// <summary>The model the run resolved to, e.g. <c>claude-opus-5[1m]</c>.</summary>
    public string? Model { get; init; }

    /// <summary>
    /// <c>permissionMode</c>, observed as <c>auto</c>. This is the mode the CLI reports it is
    /// running under — <em>measured</em>, not the one we asked for — so it is the honest source for
    /// <c>RunRecord.PermissionModeUsed</c> when the §5.3.2 fallback ladder downgrades a run
    /// (plan.md §11.4c).
    /// </summary>
    public string? PermissionMode { get; init; }

    /// <summary><c>claude_code_version</c>, e.g. <c>2.1.220</c>.</summary>
    public string? ClaudeCodeVersion { get; init; }

    /// <summary>
    /// The tool names in Claude's context. Worth asserting against: plan.md §5.3.3 passes
    /// <c>--disallowedTools "AskUserQuestion"</c> precisely so it is <em>absent</em> here.
    /// </summary>
    public IReadOnlyList<string> Tools { get; init; } = [];

    /// <summary>
    /// Plugin names from <c>plugins</c>. The real entries are objects
    /// (<c>{ name, path, source, version? }</c>) and <c>version</c> is genuinely optional — the
    /// caveman entry in the reference capture has none — so nothing here requires it.
    /// </summary>
    public IReadOnlyList<string> Plugins { get; init; } = [];

    /// <summary>Slash commands the session exposes, e.g. <c>caveman:caveman-commit</c>.</summary>
    public IReadOnlyList<string> SlashCommands { get; init; } = [];

    /// <summary>Skills the session exposes, e.g. <c>caveman:caveman</c>.</summary>
    public IReadOnlyList<string> Skills { get; init; } = [];

    /// <summary>
    /// Everything <c>plugin_errors</c> reported, as text. <b>The key is absent entirely on a healthy
    /// run</b> — it is not emitted as an empty array — so "no <c>plugin_errors</c>" means healthy,
    /// never "unparseable".
    /// </summary>
    public IReadOnlyList<string> PluginErrors { get; init; } = [];

    /// <summary>
    /// The subset of <see cref="PluginErrors"/> mentioning caveman. Surfaced separately because this
    /// is the one plugin failure that changes what the run does: <c>/caveman full</c> silently
    /// becomes a no-op and the whole session runs uncompressed (plan.md §5.2, §11.6).
    /// </summary>
    public IReadOnlyList<string> CavemanPluginErrors { get; init; } = [];

    /// <summary>
    /// Positive evidence that caveman loaded: it appears in <see cref="Plugins"/>,
    /// <see cref="SlashCommands"/>, or <see cref="Skills"/>. Better than the absence of an error,
    /// since a healthy run reports no <c>plugin_errors</c> key at all.
    /// </summary>
    public bool CavemanPluginLoaded { get; init; }

    /// <summary>True when <see cref="CavemanPluginErrors"/> is non-empty.</summary>
    public bool HasCavemanPluginError => CavemanPluginErrors.Count > 0;

    /// <summary>
    /// The check a runner should actually warn on: either caveman errored, or the session published
    /// an inventory and caveman was not in it. Stays false when the CLI reported no inventory at all,
    /// so an older or stripped-down build cannot produce a phantom warning.
    /// </summary>
    public bool CavemanUnavailable =>
        HasCavemanPluginError ||
        (!CavemanPluginLoaded &&
         (Plugins.Count > 0 || SlashCommands.Count > 0 || Skills.Count > 0));
}

/// <summary>
/// <c>system</c>/<c>hook_started</c> — a Claude Code hook is about to run. Not in plan.md §5.4;
/// observed in 2.1.220, and paired with <see cref="ClaudeHookResponseEvent"/> by <see cref="HookId"/>.
/// </summary>
public sealed record ClaudeHookStartedEvent(string RawLine, string? HookId, string? HookName)
    : ClaudeStreamEvent(RawLine)
{
    /// <summary>Which lifecycle point fired the hook, e.g. <c>SessionStart</c>.</summary>
    public string? HookEvent { get; init; }
}

/// <summary>
/// <c>system</c>/<c>hook_response</c> — how a hook finished. A hook that fails is a real cause of a
/// broken unattended run (the caveman activation hook is exactly one of these), so it gets a typed
/// event rather than being lost in the unknown bucket.
/// </summary>
public sealed record ClaudeHookResponseEvent(string RawLine, string? HookId, string? HookName)
    : ClaudeStreamEvent(RawLine)
{
    /// <summary>Which lifecycle point fired the hook, e.g. <c>SessionStart</c>.</summary>
    public string? HookEvent { get; init; }

    /// <summary><c>outcome</c>, observed as <c>success</c>.</summary>
    public string? Outcome { get; init; }

    /// <summary><c>exit_code</c>, observed as <c>0</c>.</summary>
    public int? ExitCode { get; init; }

    /// <summary>What the hook contributed to the session. Duplicated by the CLI into <see cref="Stdout"/>.</summary>
    public string? Output { get; init; }

    public string? Stdout { get; init; }

    public string? Stderr { get; init; }

    /// <summary>A non-zero exit code, or an outcome that is not <c>success</c>.</summary>
    public bool Failed =>
        ExitCode is { } code && code != 0 ||
        (Outcome is { } outcome && !string.Equals(outcome, "success", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// <c>system</c>/<c>status</c> — a coarse lifecycle ping, observed as <c>requesting</c>. Not in
/// plan.md §5.4. Cheap heartbeat material for the stall detector and the dashboard status pill.
/// </summary>
public sealed record ClaudeStatusEvent(string RawLine, string? Status) : ClaudeStreamEvent(RawLine);

/// <summary>
/// <c>rate_limit_event</c> — the CLI volunteering the subscription quota it just saw. Not in
/// plan.md §5.4, and a genuinely useful third usage source alongside §4.1 and §4.2: it arrives free,
/// mid-run, with no extra HTTP call.
/// </summary>
/// <remarks>
/// <b>Scale warning.</b> The wire field <c>utilization</c> is a <b>fraction in 0–1</b>
/// (<c>0.36</c> observed against 37% from the OAuth endpoint for the same window minutes earlier),
/// whereas <see cref="Usage.UsageWindow.UtilizationPercent"/> and everything in plan.md §4 is a
/// <b>percentage 0–100</b>. <see cref="UtilizationPercent"/> has already been multiplied by 100;
/// <see cref="UtilizationFraction"/> is the untouched wire value so the conversion stays auditable.
/// Confusing the two by 100× in an app whose entire job is a percentage threshold would either burn
/// a night of quota or never run at all.
/// </remarks>
public sealed record ClaudeRateLimitEvent(string RawLine) : ClaudeStreamEvent(RawLine)
{
    /// <summary><c>status</c>, observed as <c>allowed_warning</c>.</summary>
    public string? Status { get; init; }

    /// <summary><c>rateLimitType</c>, observed as <c>seven_day</c>; expect <c>five_hour</c> too.</summary>
    public string? RateLimitType { get; init; }

    /// <summary>The raw <c>utilization</c> exactly as it arrived — a fraction, <c>0.36</c> = 36%.</summary>
    public double? UtilizationFraction { get; init; }

    /// <summary>
    /// <see cref="UtilizationFraction"/> × 100, clamped to 0–100: the same scale as
    /// <see cref="Usage.UsageWindow.UtilizationPercent"/> and the §4.3 threshold.
    /// </summary>
    public double? UtilizationPercent { get; init; }

    /// <summary><c>resetsAt</c>, which arrives as <b>epoch seconds</b> — not milliseconds.</summary>
    public DateTimeOffset? ResetsAt { get; init; }

    /// <summary><c>isUsingOverage</c> — the account is spending extra-usage credits.</summary>
    public bool? IsUsingOverage { get; init; }
}

/// <summary>One <c>tool_use</c> block off an assistant message.</summary>
/// <param name="Id">The <c>toolu_…</c> id a later <see cref="ClaudeToolResultEvent"/> refers back to.</param>
/// <param name="Name">The tool, e.g. <c>Write</c>.</param>
/// <param name="InputJson">The arguments as compact JSON — structured data, never transcript prose.</param>
public sealed record ClaudeToolUse(string? Id, string? Name, string? InputJson);

/// <summary>
/// A complete <c>assistant</c> message, or a genuine <c>user</c> turn, flattened to the text a
/// transcript pane wants (plan.md §5.4).
/// </summary>
/// <remarks>
/// <c>message.content</c> is an <em>array of mixed block types</em>, not a string:
/// <see cref="Text"/> is the <c>text</c> blocks only, and <c>tool_use</c> blocks land in
/// <see cref="ToolUses"/> as structured data so raw argument JSON never appears in the live pane.
/// Thinking blocks are dropped entirely.
/// </remarks>
public sealed record ClaudeMessageEvent(string RawLine, ClaudeMessageRole Role, string Text)
    : ClaudeStreamEvent(RawLine)
{
    /// <summary>The model that produced an assistant message, from the nested <c>message.model</c>.</summary>
    public string? Model { get; init; }

    /// <summary>The tool calls this message made. Empty for a pure text turn.</summary>
    public IReadOnlyList<ClaudeToolUse> ToolUses { get; init; } = [];
}

/// <summary>
/// A tool's output being fed back to Claude — a <c>user</c>-typed line whose content is a
/// <c>tool_result</c> block.
/// </summary>
/// <remarks>
/// plan.md §5.4 lumps <c>assistant</c> and <c>user</c> together as "append to the live transcript
/// pane", but they are not the same thing. In an unattended run there is no human, so in practice
/// <b>every</b> <c>user</c> line is one of these. Showing it as if the absent operator had typed it
/// would be actively misleading, hence a separate event.
/// </remarks>
public sealed record ClaudeToolResultEvent(string RawLine, string? ToolUseId, string Text)
    : ClaudeStreamEvent(RawLine)
{
    /// <summary>The tool reported a failure (<c>is_error</c> on the block).</summary>
    public bool IsError { get; init; }
}

/// <summary>
/// A <c>stream_event</c>/<c>content_block_start</c>: a new content block opened. For a
/// <c>tool_use</c> block the id and name arrive <em>here</em>, before the arguments finish
/// streaming, so a UI can show "running Write…" immediately.
/// </summary>
public sealed record ClaudeContentBlockStartEvent(string RawLine, string? BlockType)
    : ClaudeStreamEvent(RawLine)
{
    /// <summary>The block's index within the message.</summary>
    public int? Index { get; init; }

    /// <summary>The <c>toolu_…</c> id, when <see cref="BlockType"/> is <c>tool_use</c>.</summary>
    public string? ToolUseId { get; init; }

    /// <summary>The tool name, when <see cref="BlockType"/> is <c>tool_use</c>.</summary>
    public string? ToolName { get; init; }

    /// <summary>Convenience for the "running X…" indicator.</summary>
    public bool IsToolUse => string.Equals(BlockType, "tool_use", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The incremental <c>event.delta.text</c> of a <c>stream_event</c> whose <c>event.type</c> is
/// <c>content_block_delta</c> and <c>event.delta.type</c> is <c>text_delta</c> — what makes the live
/// pane stream smoothly.
/// </summary>
public sealed record ClaudeTextDeltaEvent(string RawLine, string Text) : ClaudeStreamEvent(RawLine)
{
    /// <summary>The content block this fragment belongs to.</summary>
    public int? Index { get; init; }
}

/// <summary>
/// The other half of <c>content_block_delta</c>: <c>event.delta.type == "input_json_delta"</c>,
/// carrying a fragment of a tool call's arguments as <c>partial_json</c>.
/// </summary>
/// <remarks>
/// Not named by plan.md §5.4, but it is the <em>most common</em> delta in a real working run — 11 of
/// 43 lines in the reference tool-use capture. The fragments are arbitrary slices of a JSON document
/// (the first is usually <c>""</c>, and a later one can end mid-escape-sequence), so they are only
/// meaningful concatenated in order per <see cref="Index"/>. <b>They must never be appended to the
/// transcript text</b>: raw JSON shards in the live pane read as corruption, which is why they get
/// their own event instead of sharing <see cref="ClaudeTextDeltaEvent"/>.
/// </remarks>
public sealed record ClaudeInputJsonDeltaEvent(string RawLine, string PartialJson)
    : ClaudeStreamEvent(RawLine)
{
    /// <summary>The content block this fragment belongs to.</summary>
    public int? Index { get; init; }
}

/// <summary>
/// <c>system</c>/<c>api_retry</c>: the API call failed and is being retried. Drives the
/// "retrying (attempt N)" indicator of plan.md §5.4.
/// </summary>
/// <remarks>
/// Specified by plan.md but <b>not</b> present in the reference capture (the run never failed), so
/// the field names are read permissively: <c>attempt</c> / <c>attemptNumber</c> / <c>retryAttempt</c>
/// for the count, <c>delayMs</c> / <c>delay</c> for the wait, <c>error</c> / <c>message</c> for the
/// cause.
/// </remarks>
public sealed record ClaudeApiRetryEvent(string RawLine, int? Attempt) : ClaudeStreamEvent(RawLine)
{
    /// <summary>How long the CLI says it will wait before the next attempt, when it says.</summary>
    public TimeSpan? Delay { get; init; }

    /// <summary>The error text that triggered the retry, e.g. <c>Overloaded</c>.</summary>
    public string? Message { get; init; }
}

/// <summary>
/// The terminal <c>result</c> line. Everything needed to close a <see cref="History.RunRecord"/> out
/// lives here (plan.md §5.4, §8). Note the real line puts <c>type</c> near the <em>end</em> of the
/// object, so nothing may assume key ordering.
/// </summary>
public sealed record ClaudeResultEvent(string RawLine, bool IsError) : ClaudeStreamEvent(RawLine)
{
    /// <summary>Observed <c>success</c>; expect <c>error_max_turns</c> / <c>error_during_execution</c>.</summary>
    public string? Subtype { get; init; }

    /// <summary>Total spend for the session, from <c>total_cost_usd</c>.</summary>
    public double? TotalCostUsd { get; init; }

    /// <summary>Wall-clock duration, from <c>duration_ms</c>.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Time spent inside API calls, from <c>duration_api_ms</c>.</summary>
    public TimeSpan? ApiDuration { get; init; }

    /// <summary>The final assistant text — the natural source for <c>RunRecord.Summary</c>.</summary>
    public string? ResultText { get; init; }

    /// <summary><c>stop_reason</c>, observed as <c>end_turn</c>.</summary>
    public string? StopReason { get; init; }

    /// <summary><c>terminal_reason</c>, observed as <c>completed</c>.</summary>
    public string? TerminalReason { get; init; }

    /// <summary><c>api_error_status</c> — null on success.</summary>
    public string? ApiErrorStatus { get; init; }

    /// <summary><c>num_turns</c>.</summary>
    public int? NumTurns { get; init; }

    /// <summary>
    /// <c>permission_denials</c>, flattened to text. Empty on the reference run; anything here means
    /// the autonomy setup of plan.md §5.3 let something through that then got blocked.
    /// </summary>
    public IReadOnlyList<string> PermissionDenials { get; init; } = [];
}

/// <summary>
/// Valid JSON we do not model. Keeps the raw line so a format change shows up in the log instead of
/// vanishing. <see cref="Subtype"/> holds the inner <c>event.type</c> for <c>stream_event</c> lines
/// (<c>message_start</c>, <c>content_block_stop</c>, <c>message_delta</c>, <c>message_stop</c>),
/// which is what lets a caller filter out the high-volume partial-message chatter.
/// </summary>
public sealed record ClaudeUnknownEvent(string RawLine, string? Type, string? Subtype)
    : ClaudeStreamEvent(RawLine);

/// <summary>
/// A line that looked like JSON (it started with <c>{</c>) but would not parse, or parsed to
/// something other than an object. Worth logging loudly — it usually means the schema moved.
/// </summary>
public sealed record ClaudeMalformedLineEvent(string RawLine, string Error) : ClaudeStreamEvent(RawLine);

/// <summary>
/// A line that was never JSON at all: npm's "Need to install the following packages", a shell
/// banner, a progress spinner. Expected noise on Windows, where the CLI is reached through a
/// <c>.cmd</c> shim (plan.md §5.1, §11.7). Log at debug and move on.
/// </summary>
public sealed record ClaudeNonJsonLineEvent(string RawLine) : ClaudeStreamEvent(RawLine);

/// <summary>
/// The buffer guard tripped: a "line" grew past <see cref="Limit"/> characters without a newline, so
/// it was abandoned and everything up to the next newline is discarded. <see cref="RawLine"/> holds
/// only a short prefix, for diagnostics.
/// </summary>
/// <remarks>
/// This exists so a wedged or binary-spewing child process cannot grow the parser's buffer until the
/// app dies — an unattended run must fail loudly and cheaply, never by exhausting memory.
/// </remarks>
public sealed record ClaudeOversizedLineEvent(string RawLine, int Limit) : ClaudeStreamEvent(RawLine);
