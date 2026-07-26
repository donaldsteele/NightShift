using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NightShift.Core.Execution;

/// <summary>
/// Decodes Claude Code's <c>--output-format stream-json --verbose --include-partial-messages</c>
/// stdout into typed <see cref="ClaudeStreamEvent"/>s (plan.md §5.4).
/// </summary>
/// <remarks>
/// <para>
/// <b>This class must be impossible to crash.</b> It is the only thing between a third-party stream
/// format change and an unattended overnight run: an exception escaping here would kill the reader
/// loop mid-run, orphaning a <c>claude</c> process and losing the transcript. So no stream content
/// throws, ever — broken JSON, truncated lines, binary garbage and unknown shapes all come back as
/// events (<see cref="ClaudeMalformedLineEvent"/>, <see cref="ClaudeNonJsonLineEvent"/>,
/// <see cref="ClaudeOversizedLineEvent"/>, <see cref="ClaudeUnknownEvent"/>) that the caller can log
/// and ignore. The only exceptions raised are for caller mistakes (a null reader, a nonsensical
/// buffer limit), which happen at wire-up time and not from data.
/// </para>
/// <para>
/// <b>Wire format.</b> Newline-delimited JSON, one object per line. Three things about the real
/// stream drive the design: stdout arrives in arbitrary chunks, so a single JSON object routinely
/// spans several reads and must be buffered until its newline; on Windows the CLI is reached through
/// an npm <c>.cmd</c> shim (plan.md §5.1, §11.7), which is free to interleave its own non-JSON lines;
/// and line endings may be either LF or CRLF.
/// </para>
/// <para>
/// Parsing is weakly typed on purpose, exactly as <see cref="Usage.CcusageProvider"/> is: this is a
/// third-party schema that has already moved once, so it is walked with <see cref="JsonNode"/> and
/// never bound to a model or added to a source-generated context. Fields are read permissively —
/// missing keys are absences, not errors, and both snake_case and camelCase spellings are tried.
/// </para>
/// <para>
/// <b>Not thread-safe.</b> Feed it from a single reader loop. Use one instance per stream (stdout and
/// stderr each need their own), and call <see cref="Flush"/> when the stream ends so a final
/// unterminated line is not lost.
/// </para>
/// </remarks>
public sealed class StreamJsonParser
{
    /// <summary>
    /// How long one line may get before the buffer guard trips. Generous — a single
    /// <c>hook_response</c> in the reference capture is already 8 KB and a large tool result is far
    /// bigger — but bounded, because "no newline ever arrives" must not mean "grow until the app
    /// dies".
    /// </summary>
    public const int DefaultMaxLineLength = 4 * 1024 * 1024;

    /// <summary>How much of an oversized line is kept for diagnostics.</summary>
    public const int OversizedPreviewLength = 512;

    const int ReadBufferLength = 8 * 1024;
    const int MaxContentDepth = 8;

    static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    readonly StringBuilder _line = new();

    bool _discarding;

    /// <param name="maxLineLength">Overrides <see cref="DefaultMaxLineLength"/>; tests use a tiny value.</param>
    public StreamJsonParser(int maxLineLength = DefaultMaxLineLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLineLength, 1);
        MaxLineLength = maxLineLength;
    }

    /// <summary>The buffer guard's ceiling, in characters.</summary>
    public int MaxLineLength { get; }

    /// <summary>Characters currently held back waiting for a newline.</summary>
    public int BufferedLength => _line.Length;

    // ── Feed-style API ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pushes one chunk of stdout and returns the events it completed. A chunk may contain any number
    /// of whole lines, none at all, or the tail of one line and the head of the next; only lines that
    /// were terminated by this chunk produce events.
    /// </summary>
    public IReadOnlyList<ClaudeStreamEvent> Feed(string? chunk) =>
        string.IsNullOrEmpty(chunk) ? [] : Feed(chunk.AsSpan());

    /// <summary>Overload for <c>StreamReader.Read(char[], int, int)</c> callers.</summary>
    public IReadOnlyList<ClaudeStreamEvent> Feed(char[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return count <= 0 ? [] : Feed(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc cref="Feed(string)"/>
    public IReadOnlyList<ClaudeStreamEvent> Feed(ReadOnlySpan<char> chunk)
    {
        List<ClaudeStreamEvent>? events = null;

        while (!chunk.IsEmpty)
        {
            var newline = chunk.IndexOf('\n');
            var segment = newline < 0 ? chunk : chunk[..newline];

            if (!_discarding && !segment.IsEmpty)
            {
                // Take only what fits under the ceiling; anything past it means this "line" is
                // pathological and the rest of it is dropped on the floor until a newline shows up.
                var room = MaxLineLength - _line.Length;
                if (segment.Length <= room)
                {
                    _line.Append(segment);
                }
                else
                {
                    _line.Append(segment[..room]);
                    (events ??= []).Add(TripBufferGuard());
                }
            }

            if (newline < 0)
            {
                break;
            }

            if (CompleteLine() is { } completed)
            {
                (events ??= []).Add(completed);
            }

            chunk = chunk[(newline + 1)..];
        }

        return events ?? [];
    }

    /// <summary>
    /// Ends the stream: decodes whatever is still buffered without its newline. The CLI's last line
    /// is usually terminated, but a killed process (timeout, stall detector — plan.md §5.3.3, §5.4)
    /// can leave one dangling, and that line is often the <c>result</c>.
    /// </summary>
    public IReadOnlyList<ClaudeStreamEvent> Flush()
    {
        if (_discarding)
        {
            Reset();
            return [];
        }

        if (_line.Length == 0)
        {
            return [];
        }

        return CompleteLine() is { } completed ? [completed] : [];
    }

    /// <summary>Drops any partial line. For reusing one parser across runs.</summary>
    public void Reset()
    {
        _line.Clear();
        _discarding = false;
    }

    // ── Whole-stream conveniences ──────────────────────────────────────────────────────────────

    /// <summary>Parses a complete stream held in memory, flushing any unterminated final line.</summary>
    public static IReadOnlyList<ClaudeStreamEvent> ParseAll(
        string? text,
        int maxLineLength = DefaultMaxLineLength)
    {
        var parser = new StreamJsonParser(maxLineLength);
        var events = new List<ClaudeStreamEvent>(parser.Feed(text));
        events.AddRange(parser.Flush());
        return events;
    }

    /// <summary>Drains a reader synchronously. Used by the fixture tests.</summary>
    public static IReadOnlyList<ClaudeStreamEvent> ParseAll(
        TextReader reader,
        int maxLineLength = DefaultMaxLineLength)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var parser = new StreamJsonParser(maxLineLength);
        var events = new List<ClaudeStreamEvent>();
        var buffer = new char[ReadBufferLength];

        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            events.AddRange(parser.Feed(buffer, 0, read));
        }

        events.AddRange(parser.Flush());
        return events;
    }

    /// <summary>
    /// Streams a reader, yielding each event as its line completes. This is the shape the headless
    /// runner wants: it reads chunks rather than lines, so a half-written line never stalls the
    /// stall detector, and events reach the live pane as they arrive.
    /// </summary>
    public static async IAsyncEnumerable<ClaudeStreamEvent> ParseAsync(
        TextReader reader,
        int maxLineLength = DefaultMaxLineLength,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var parser = new StreamJsonParser(maxLineLength);
        var buffer = new char[ReadBufferLength];

        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            foreach (var e in parser.Feed(buffer, 0, read))
            {
                yield return e;
            }
        }

        foreach (var e in parser.Flush())
        {
            yield return e;
        }
    }

    /// <summary>
    /// Decodes one already-complete line. Returns null for a blank line — blank lines carry no
    /// meaning and must not reach the transcript.
    /// </summary>
    public static ClaudeStreamEvent? ParseLine(string? line) => Decode(line ?? string.Empty);

    // ── Line assembly ──────────────────────────────────────────────────────────────────────────

    ClaudeStreamEvent? CompleteLine()
    {
        if (_discarding)
        {
            // The oversized event went out the moment the guard tripped; the remainder is silent.
            _discarding = false;
            _line.Clear();
            return null;
        }

        // CRLF: the '\r' was buffered along with the rest of the line.
        if (_line.Length > 0 && _line[^1] == '\r')
        {
            _line.Length--;
        }

        var line = _line.ToString();
        _line.Clear();
        return Decode(line);
    }

    ClaudeOversizedLineEvent TripBufferGuard()
    {
        var preview = _line.ToString(0, Math.Min(_line.Length, OversizedPreviewLength));
        _line.Clear();
        _discarding = true;
        return new ClaudeOversizedLineEvent(preview, MaxLineLength);
    }

    // ── Decoding ───────────────────────────────────────────────────────────────────────────────

    static ClaudeStreamEvent? Decode(string rawLine)
    {
        var trimmed = rawLine.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        // NDJSON here is always one *object* per line. Anything else is shim or shell noise, and
        // calling that "malformed JSON" would cry wolf on every npm banner.
        if (trimmed[0] != '{')
        {
            return new ClaudeNonJsonLineEvent(rawLine);
        }

        try
        {
            if (JsonNode.Parse(trimmed, NodeOptions, DocumentOptions) is not JsonObject obj)
            {
                return new ClaudeMalformedLineEvent(rawLine, "Expected a JSON object.");
            }

            return Map(rawLine, obj);
        }
        catch (Exception ex)
        {
            // Deliberately unfiltered. JsonException covers the known cases (bad syntax, depth
            // limit), but the contract of this class is that *nothing* from the stream escapes, and
            // a new System.Text.Json failure mode must not be the thing that ends an overnight run.
            return new ClaudeMalformedLineEvent(rawLine, ex.Message);
        }
    }

    static ClaudeStreamEvent Map(string rawLine, JsonObject obj)
    {
        var type = ReadString(obj["type"]);
        var subtype = ReadString(obj["subtype"]);

        if (Is(type, "system"))
        {
            if (Is(subtype, "init"))
            {
                return MapInit(rawLine, obj);
            }

            if (Is(subtype, "api_retry"))
            {
                return MapApiRetry(rawLine, obj);
            }

            if (Is(subtype, "hook_started"))
            {
                return WithEnvelope(
                    new ClaudeHookStartedEvent(
                        rawLine,
                        ReadString(obj["hook_id"] ?? obj["hookId"]),
                        ReadString(obj["hook_name"] ?? obj["hookName"]))
                    {
                        HookEvent = ReadString(obj["hook_event"] ?? obj["hookEvent"]),
                    },
                    obj);
            }

            if (Is(subtype, "hook_response"))
            {
                return MapHookResponse(rawLine, obj);
            }

            if (Is(subtype, "status"))
            {
                return WithEnvelope(new ClaudeStatusEvent(rawLine, ReadString(obj["status"])), obj);
            }

            return WithEnvelope(new ClaudeUnknownEvent(rawLine, type, subtype), obj);
        }

        if (Is(type, "assistant"))
        {
            return MapMessage(rawLine, obj, ClaudeMessageRole.Assistant);
        }

        if (Is(type, "user"))
        {
            return MapMessage(rawLine, obj, ClaudeMessageRole.User);
        }

        if (Is(type, "stream_event"))
        {
            return MapStreamEvent(rawLine, obj);
        }

        if (Is(type, "rate_limit_event"))
        {
            return MapRateLimit(rawLine, obj);
        }

        if (Is(type, "result"))
        {
            return MapResult(rawLine, obj);
        }

        return WithEnvelope(new ClaudeUnknownEvent(rawLine, type, subtype), obj);
    }

    /// <summary>Stamps the fields every line carries onto the event, in one place.</summary>
    static T WithEnvelope<T>(T e, JsonObject obj) where T : ClaudeStreamEvent
    {
        e.SessionId = ReadString(obj["session_id"] ?? obj["sessionId"]);
        e.ParentToolUseId = ReadString(obj["parent_tool_use_id"] ?? obj["parentToolUseId"]);
        e.Uuid = ReadString(obj["uuid"]);
        return e;
    }

    static ClaudeInitEvent MapInit(string rawLine, JsonObject obj)
    {
        var plugins = ReadNames(obj["plugins"]);
        var slashCommands = ReadNames(obj["slash_commands"] ?? obj["slashCommands"]);
        var skills = ReadNames(obj["skills"]);

        // A healthy run omits `plugin_errors` entirely rather than sending an empty array, so its
        // absence is good news. The reliable signal for "did caveman load" is therefore the positive
        // one: caveman shows up as a plugin, a skill, or a `caveman:*` slash command.
        var pluginErrors = ReadTexts(obj["plugin_errors"] ?? obj["pluginErrors"]);

        return WithEnvelope(
            new ClaudeInitEvent(rawLine)
            {
                Cwd = ReadString(obj["cwd"]),
                Model = ReadString(obj["model"]),
                PermissionMode = ReadString(obj["permissionMode"] ?? obj["permission_mode"]),
                ClaudeCodeVersion = ReadString(obj["claude_code_version"] ?? obj["claudeCodeVersion"]),
                Tools = ReadNames(obj["tools"]),
                Plugins = plugins,
                SlashCommands = slashCommands,
                Skills = skills,
                PluginErrors = pluginErrors,
                CavemanPluginErrors = [.. pluginErrors.Where(MentionsCaveman)],
                CavemanPluginLoaded =
                    plugins.Any(MentionsCaveman) ||
                    slashCommands.Any(MentionsCaveman) ||
                    skills.Any(MentionsCaveman),
            },
            obj);
    }

    static ClaudeHookResponseEvent MapHookResponse(string rawLine, JsonObject obj) =>
        WithEnvelope(
            new ClaudeHookResponseEvent(
                rawLine,
                ReadString(obj["hook_id"] ?? obj["hookId"]),
                ReadString(obj["hook_name"] ?? obj["hookName"]))
            {
                HookEvent = ReadString(obj["hook_event"] ?? obj["hookEvent"]),
                Outcome = ReadString(obj["outcome"]),
                ExitCode = ReadInt(obj["exit_code"] ?? obj["exitCode"]),
                Output = ReadString(obj["output"]),
                Stdout = ReadString(obj["stdout"]),
                Stderr = ReadString(obj["stderr"]),
            },
            obj);

    static ClaudeApiRetryEvent MapApiRetry(string rawLine, JsonObject obj)
    {
        var attempt =
            ReadInt(obj["attempt"]) ??
            ReadInt(obj["attemptNumber"] ?? obj["attempt_number"]) ??
            ReadInt(obj["retryAttempt"] ?? obj["retry_attempt"]) ??
            ReadInt(obj["retryCount"] ?? obj["retry_count"]);

        var delayMs = ReadDouble(obj["delayMs"] ?? obj["delay_ms"] ?? obj["delay"] ?? obj["retryAfterMs"]);

        return WithEnvelope(
            new ClaudeApiRetryEvent(rawLine, attempt)
            {
                Delay = ToDuration(delayMs),
                Message = ReadString(obj["error"]) ?? ReadString(obj["message"]) ?? ReadString(obj["reason"]),
            },
            obj);
    }

    static ClaudeRateLimitEvent MapRateLimit(string rawLine, JsonObject obj)
    {
        // The payload nests under `rate_limit_info`; tolerate it being flattened on the line itself.
        var info = (obj["rate_limit_info"] ?? obj["rateLimitInfo"]) as JsonObject ?? obj;

        var fraction = ReadDouble(info["utilization"]);

        // ── The 100× conversion. `utilization` is a FRACTION (0.36 == 36%), while plan.md §4 and
        // UsageWindow.UtilizationPercent are PERCENTAGES (0–100). Verified against the OAuth usage
        // endpoint, which reported 37% for the same seven_day window minutes after this line was
        // captured with 0.36. Multiply exactly once, here, and keep the raw value beside it so the
        // scale is never ambiguous downstream.
        var percent = fraction is { } f && double.IsFinite(f)
            ? Math.Clamp(f * 100d, 0d, 100d)
            : (double?)null;

        return WithEnvelope(
            new ClaudeRateLimitEvent(rawLine)
            {
                Status = ReadString(info["status"]),
                RateLimitType = ReadString(info["rateLimitType"] ?? info["rate_limit_type"]),
                UtilizationFraction = fraction,
                UtilizationPercent = percent,
                ResetsAt = ReadTimestamp(info["resetsAt"] ?? info["resets_at"]),
                IsUsingOverage = ReadBool(info["isUsingOverage"] ?? info["is_using_overage"]),
            },
            obj);
    }

    static ClaudeStreamEvent MapMessage(string rawLine, JsonObject obj, ClaudeMessageRole role)
    {
        var message = obj["message"] as JsonObject;
        var content = message?["content"] ?? obj["content"];

        // A `user` line in an unattended run is never the operator — it is a tool handing its output
        // back. Labelling that as a user turn in the transcript would invent a human who isn't there.
        if (FindToolResult(content, depth: 0) is { } toolResult)
        {
            return WithEnvelope(
                new ClaudeToolResultEvent(
                    rawLine,
                    ReadString(toolResult["tool_use_id"] ?? toolResult["toolUseId"]),
                    ExtractText(content, depth: 0))
                {
                    IsError = ReadBool(toolResult["is_error"] ?? toolResult["isError"]) ?? false,
                },
                obj);
        }

        return WithEnvelope(
            new ClaudeMessageEvent(rawLine, role, ExtractText(content, depth: 0))
            {
                Model = ReadString(message?["model"]),
                ToolUses = ReadToolUses(content),
            },
            obj);
    }

    static ClaudeStreamEvent MapStreamEvent(string rawLine, JsonObject obj)
    {
        var inner = obj["event"] as JsonObject;
        var innerType = ReadString(inner?["type"]);
        var index = ReadInt(inner?["index"]);
        var delta = inner?["delta"] as JsonObject;
        var deltaType = ReadString(delta?["type"]);

        if (Is(deltaType, "text_delta"))
        {
            return WithEnvelope(
                new ClaudeTextDeltaEvent(rawLine, ReadString(delta?["text"]) ?? string.Empty)
                {
                    Index = index,
                },
                obj);
        }

        if (Is(deltaType, "input_json_delta"))
        {
            // Deliberately not a text delta: these are shards of an arguments document and would
            // read as corruption if they reached the live pane.
            return WithEnvelope(
                new ClaudeInputJsonDeltaEvent(
                    rawLine,
                    ReadString(delta?["partial_json"] ?? delta?["partialJson"]) ?? string.Empty)
                {
                    Index = index,
                },
                obj);
        }

        if (Is(innerType, "content_block_start"))
        {
            var block = inner?["content_block"] as JsonObject ?? inner?["contentBlock"] as JsonObject;
            return WithEnvelope(
                new ClaudeContentBlockStartEvent(rawLine, ReadString(block?["type"]))
                {
                    Index = index,
                    ToolUseId = ReadString(block?["id"]),
                    ToolName = ReadString(block?["name"]),
                },
                obj);
        }

        // message_start / content_block_stop / message_delta / message_stop, plus thinking_delta.
        // High volume and nothing the UI needs, but the inner type is carried so a caller can filter
        // rather than string-match the raw line.
        return WithEnvelope(new ClaudeUnknownEvent(rawLine, "stream_event", innerType ?? deltaType), obj);
    }

    static ClaudeResultEvent MapResult(string rawLine, JsonObject obj) =>
        WithEnvelope(
            new ClaudeResultEvent(rawLine, ReadBool(obj["is_error"] ?? obj["isError"]) ?? false)
            {
                Subtype = ReadString(obj["subtype"]),
                TotalCostUsd = ReadDouble(obj["total_cost_usd"] ?? obj["totalCostUsd"] ?? obj["cost_usd"]),
                Duration = ToDuration(ReadDouble(obj["duration_ms"] ?? obj["durationMs"])),
                ApiDuration = ToDuration(ReadDouble(obj["duration_api_ms"] ?? obj["durationApiMs"])),
                ResultText = ReadString(obj["result"]),
                StopReason = ReadString(obj["stop_reason"] ?? obj["stopReason"]),
                TerminalReason = ReadString(obj["terminal_reason"] ?? obj["terminalReason"]),
                ApiErrorStatus = ReadString(obj["api_error_status"] ?? obj["apiErrorStatus"]),
                NumTurns = ReadInt(obj["num_turns"] ?? obj["numTurns"]),
                PermissionDenials = ReadTexts(obj["permission_denials"] ?? obj["permissionDenials"]),
            },
            obj);

    // ── Field readers ──────────────────────────────────────────────────────────────────────────

    static bool MentionsCaveman(string text) =>
        text.Contains(ClaudeInitEvent.CavemanName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The first <c>tool_result</c> block in a message's content, if there is one.</summary>
    static JsonObject? FindToolResult(JsonNode? node, int depth)
    {
        if (node is null || depth > MaxContentDepth)
        {
            return null;
        }

        if (node is JsonObject obj)
        {
            return Is(ReadString(obj["type"]), "tool_result") ? obj : null;
        }

        if (node is JsonArray array)
        {
            foreach (var element in array)
            {
                if (FindToolResult(element, depth + 1) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The <c>tool_use</c> blocks of an assistant message. Kept structured rather than folded into
    /// the transcript text, so a UI can render "Write(hello.txt)" instead of a wall of argument JSON.
    /// </summary>
    static IReadOnlyList<ClaudeToolUse> ReadToolUses(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }

        List<ClaudeToolUse>? uses = null;
        foreach (var element in array)
        {
            if (element is JsonObject block && Is(ReadString(block["type"]), "tool_use"))
            {
                (uses ??= []).Add(new ClaudeToolUse(
                    ReadString(block["id"]),
                    ReadString(block["name"]),
                    block["input"] is { } input ? Compact(input) : null));
            }
        }

        return uses ?? (IReadOnlyList<ClaudeToolUse>)[];
    }

    /// <summary>
    /// Flattens a content tree to the text a transcript pane shows: plain <c>text</c> blocks, plus
    /// the body of <c>tool_result</c> blocks (which is the whole of a <c>user</c> message). Thinking
    /// and <c>tool_use</c> blocks are skipped — they are structured data, not transcript prose.
    /// </summary>
    static string ExtractText(JsonNode? node, int depth)
    {
        if (node is null || depth > MaxContentDepth)
        {
            return string.Empty;
        }

        if (node is JsonValue value)
        {
            return value.TryGetValue<string>(out var text) ? text : string.Empty;
        }

        if (node is JsonArray array)
        {
            var parts = new List<string>(array.Count);
            foreach (var element in array)
            {
                var part = ExtractText(element, depth + 1);
                if (part.Length > 0)
                {
                    parts.Add(part);
                }
            }

            return string.Join('\n', parts);
        }

        if (node is JsonObject obj)
        {
            var type = ReadString(obj["type"]);

            if (Is(type, "text") || (type is null && obj["text"] is not null))
            {
                return ReadString(obj["text"]) ?? string.Empty;
            }

            if (Is(type, "tool_result"))
            {
                return ExtractText(obj["content"], depth + 1);
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Reads a list of names. Elements may be bare strings (<c>tools</c>, <c>skills</c>,
    /// <c>slash_commands</c>) or objects (<c>plugins</c>), in which case the name-ish property wins
    /// and the compact JSON is the last resort.
    /// </summary>
    static IReadOnlyList<string> ReadNames(JsonNode? node) => ReadList(node, preferName: true);

    /// <summary>
    /// Reads a list as text, keeping whole objects rather than reducing them to a name — used where
    /// the detail matters (<c>plugin_errors</c>, <c>permission_denials</c>).
    /// </summary>
    static IReadOnlyList<string> ReadTexts(JsonNode? node) => ReadList(node, preferName: false);

    static IReadOnlyList<string> ReadList(JsonNode? node, bool preferName)
    {
        switch (node)
        {
            case null:
                return [];

            case JsonValue single:
                // A scalar where a list was expected — take it rather than losing the reading.
                return single.TryGetValue<string>(out var text) ? [text] : [Compact(single)];

            case JsonArray array:
            {
                var items = new List<string>(array.Count);
                foreach (var element in array)
                {
                    if (element is null)
                    {
                        continue;
                    }

                    if (element is JsonValue elementValue && elementValue.TryGetValue<string>(out var elementText))
                    {
                        items.Add(elementText);
                        continue;
                    }

                    if (preferName && element is JsonObject elementObject &&
                        (ReadString(elementObject["name"]) ??
                         ReadString(elementObject["plugin"]) ??
                         ReadString(elementObject["id"])) is { } name)
                    {
                        items.Add(name);
                        continue;
                    }

                    items.Add(Compact(element));
                }

                return items;
            }

            case JsonObject map:
            {
                // `{ "caveman": "failed to load" }` — a shape ccusage-style payloads have used for
                // error maps. Keep the key so a caveman mention is still findable.
                var items = new List<string>(map.Count);
                foreach (var (key, child) in map)
                {
                    var childText = child is JsonValue childValue && childValue.TryGetValue<string>(out var s)
                        ? s
                        : Compact(child);
                    items.Add(childText.Length == 0 ? key : $"{key}: {childText}");
                }

                return items;
            }

            default:
                return [];
        }
    }

    static string Compact(JsonNode? node)
    {
        try
        {
            return node?.ToJsonString() ?? string.Empty;
        }
        catch (Exception)
        {
            // Serialising a node we only ever read should not fail, but this class does not get to
            // throw for stream content.
            return string.Empty;
        }
    }

    static bool Is(string? value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    static double? ReadDouble(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<double>(out var number))
        {
            return number;
        }

        // Numbers have shipped as JSON strings before (plan.md §4.2 hit the same thing).
        return value.TryGetValue<string>(out var text) &&
               double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    static int? ReadInt(JsonNode? node) =>
        ReadDouble(node) is { } number && double.IsFinite(number) &&
        number >= int.MinValue && number <= int.MaxValue
            ? (int)number
            : null;

    static bool? ReadBool(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<bool>(out var flag))
        {
            return flag;
        }

        return value.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed)
            ? parsed
            : null;
    }

    static TimeSpan? ToDuration(double? milliseconds)
    {
        if (milliseconds is not { } ms || !double.IsFinite(ms) || ms < 0)
        {
            return null;
        }

        try
        {
            return TimeSpan.FromMilliseconds(ms);
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentException)
        {
            // A duration that does not fit in a TimeSpan is nonsense, not a reason to stop parsing.
            return null;
        }
    }

    /// <summary>
    /// Reads a timestamp. <c>rate_limit_info.resetsAt</c> arrives as <b>epoch seconds</b>
    /// (<c>1785621600</c>) — unlike the credentials file's <c>expiresAt</c>, which is milliseconds
    /// (plan.md §4.1) — so a magnitude check picks the unit rather than a hardcoded assumption, and
    /// an ISO-8601 string is accepted too because that is what the OAuth endpoint sends.
    /// </summary>
    static DateTimeOffset? ReadTimestamp(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (ReadDouble(node) is { } number && double.IsFinite(number) && number > 0)
        {
            // 1e11 seconds is the year 5138; anything larger is milliseconds, not a date in seconds.
            return number < 1e11
                ? FromUnix(number, seconds: true)
                : number < 1e14
                    ? FromUnix(number, seconds: false)
                    : null;
        }

        return value.TryGetValue<string>(out var text) &&
               DateTimeOffset.TryParse(
                   text,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                   out var parsed)
            ? parsed
            : null;
    }

    static DateTimeOffset? FromUnix(double value, bool seconds)
    {
        try
        {
            return seconds
                ? DateTimeOffset.FromUnixTimeSeconds((long)value)
                : DateTimeOffset.FromUnixTimeMilliseconds((long)value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
