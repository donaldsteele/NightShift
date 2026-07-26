using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudePilot.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace ClaudePilot.Core.Usage;

/// <summary>One invocation of the ccusage CLI: a program plus its already-split arguments.</summary>
/// <remarks>
/// Kept as a record of <c>FileName</c> + <c>Arguments</c> rather than a raw command line so the
/// runner can use <see cref="ProcessStartInfo.ArgumentList"/> and never hand-quote (plan.md §5.1
/// makes the same point about the Claude CLI).
/// </remarks>
public sealed record CcusageCommand(string FileName, IReadOnlyList<string> Arguments)
{
    public override string ToString() =>
        Arguments.Count == 0 ? FileName : $"{FileName} {string.Join(' ', Arguments)}";
}

/// <summary>What the ccusage process produced. <paramref name="TimedOut"/> is not a failure exit code.</summary>
public sealed record CcusageResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut = false);

/// <summary>
/// Spawns the ccusage command. Exists purely so <see cref="CcusageProvider"/> can be unit tested
/// without a real <c>npx</c> on the machine — plan.md §4.2 requires fixture-driven parse tests.
/// </summary>
public interface ICcusageProcessRunner
{
    Task<CcusageResult> RunAsync(CcusageCommand command, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// The real runner. Never throws for an unstartable program — a missing <c>npx</c> is an expected
/// condition on a machine without Node, not an error the scheduler should see.
/// </summary>
public sealed class CcusageProcessRunner : ICcusageProcessRunner
{
    public async Task<CcusageResult> RunAsync(
        CcusageCommand command,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        using var process = new Process { StartInfo = BuildStartInfo(command) };
        process.Start();

        // Anything reading stdin would block forever in an unattended run (plan.md §5.3.3).
        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            KillTree(process);
            return new CcusageResult(-1, string.Empty, string.Empty, TimedOut: true);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            throw;
        }

        return new CcusageResult(
            process.ExitCode,
            await stdout.ConfigureAwait(false),
            await stderr.ConfigureAwait(false));
    }

    static ProcessStartInfo BuildStartInfo(CcusageCommand command)
    {
        var info = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // `npx` on Windows is a .cmd shim, and CreateProcess only ever appends .exe — launching it
        // directly fails with "file not found" (plan.md §11.7). Route extensionless programs
        // through cmd.exe, which does honour PATHEXT.
        if (OperatingSystem.IsWindows() && !Path.HasExtension(command.FileName))
        {
            info.FileName = "cmd.exe";
            info.ArgumentList.Add("/c");
            info.ArgumentList.Add(command.FileName);
        }
        else
        {
            info.FileName = command.FileName;
        }

        foreach (var argument in command.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }

    static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                       or System.ComponentModel.Win32Exception)
        {
            // The process died on its own between the check and the kill. Nothing to clean up.
        }
    }
}

/// <summary>
/// Fallback usage provider (plan.md §4.2). <c>ccusage</c> reads the local <c>~/.claude</c> JSONL
/// transcripts and reports <em>tokens</em>, not plan quota, so the percentage here is only ever an
/// estimate — hence <see cref="UsageSnapshot.IsApproximate"/> is always <c>true</c>.
/// </summary>
/// <remarks>
/// <para>
/// The parser is deliberately schema-agnostic. Published ccusage output disagrees on the envelope
/// (<c>{ "blocks": [...] }</c> versus <c>{ "type": "blocks", "data": [...], "summary": {...} }</c>)
/// and on where the token counts live (<c>tokenCounts.inputTokens</c> versus a flat
/// <c>inputTokens</c>). Rather than binding a model per version, we walk the JSON for the first
/// array holding an object with <c>isActive: true</c> and then try every known nesting for each
/// field. Nothing here is added to the source-generated JSON contexts — this payload belongs to a
/// third-party tool and must stay weakly typed.
/// </para>
/// <para>
/// Verified against <b>ccusage 20.0.18 on 2026-07-26</b> by running the real CLI; the fixtures
/// named <c>ccusage-real-*</c> in the test project are that captured output byte for byte. The
/// schema has shifted between versions before, so re-verify against a live run before assuming a
/// field is gone rather than moved.
/// </para>
/// </remarks>
public sealed class CcusageProvider : IUsageProvider
{
    /// <summary>
    /// plan.md §4.2, corrected against a live ccusage 20.0.18 run. <c>--token-limit max</c> means
    /// "the highest block previously observed", which ccusage can only work out from the full block
    /// history.
    /// <para>
    /// <b>Do not add <c>--active</c> back.</b> It filters the history down to the current block, so
    /// there is nothing left to take a maximum over, and ccusage then silently omits
    /// <c>tokenLimitStatus</c> entirely instead of erroring — leaving us with no denominator and
    /// killing the whole fallback path. Selecting the active block is free for us anyway: the
    /// parser already walks for the first <c>isActive: true</c> element. (An explicit numeric
    /// <c>--token-limit 500000</c> does survive <c>--active</c>, which is why an override may
    /// legitimately use both.)
    /// </para>
    /// </summary>
    public const string DefaultCommand = "npx ccusage@latest blocks --json --token-limit max";

    /// <summary>Generous, because the first <c>npx</c> call downloads the package before it runs.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    const int MaxSearchDepth = 12;
    const int MaxReasonDetailLength = 200;

    static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    readonly ISettingsStore _settingsStore;
    readonly ICcusageProcessRunner _runner;
    readonly ILogger<CcusageProvider> _logger;
    readonly TimeProvider _timeProvider;

    public CcusageProvider(
        ISettingsStore settingsStore,
        ICcusageProcessRunner runner,
        ILogger<CcusageProvider> logger,
        TimeProvider? timeProvider = null)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public UsageSource Source => UsageSource.Ccusage;

    /// <summary>How long the process gets before it is killed and the snapshot reported unavailable.</summary>
    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    /// <summary>
    /// Runs ccusage and turns the active 5-hour block into a snapshot. Every expected failure —
    /// no Node, non-zero exit, garbage output, no active block, no resolvable token limit — comes
    /// back as <see cref="UsageSnapshot.Unavailable"/>; the scheduler decides what to do with that
    /// via <c>OnUsageUnavailable</c> (plan.md §4.3), so throwing here would only crash a tick.
    /// </summary>
    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        var command = ResolveCommand(_settingsStore.Current);

        CcusageResult result;
        try
        {
            result = await _runner.RunAsync(command, Timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ccusage could not be started ({Command}).", command);
            return Unavailable($"ccusage could not be run: {Detail(ex.Message)}");
        }

        if (result.TimedOut)
        {
            _logger.LogWarning("ccusage timed out after {Timeout}.", Timeout);
            return Unavailable(
                $"ccusage did not finish within {Timeout.TotalSeconds:0} seconds.");
        }

        if (result.ExitCode != 0)
        {
            var detail = Detail(FirstLine(result.StandardError));
            _logger.LogDebug("ccusage exited with {ExitCode}: {Detail}", result.ExitCode, detail);
            return Unavailable(detail.Length == 0
                ? $"ccusage exited with code {result.ExitCode}."
                : $"ccusage exited with code {result.ExitCode}: {detail}");
        }

        if (!TryExtractJson(result.StandardOutput, out var json))
        {
            return Unavailable("ccusage produced no JSON output.");
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, NodeOptions, DocumentOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "ccusage output was not valid JSON.");
            return Unavailable("ccusage output was not valid JSON.");
        }

        if (root is null)
        {
            return Unavailable("ccusage output was not valid JSON.");
        }

        if (FindActiveBlock(root, depth: 0) is not { } block)
        {
            return Unavailable("ccusage reported no active 5-hour block.");
        }

        // ccusage does the division itself whenever it resolved a limit, and its own figure is the
        // one the user sees in `ccusage blocks`; matching it avoids two tools disagreeing by a
        // rounding step. Recomputing from the raw counts is the fallback for payloads that carry a
        // limit but no percentage.
        double percent;
        if (TryReadPercentUsed(block, root) is { } reportedPercent)
        {
            percent = Math.Clamp(reportedPercent, 0d, 100d);
            _logger.LogDebug("ccusage reported the active block at {Percent:0.0}%.", percent);
        }
        else
        {
            if (TryReadTotalTokens(block) is not { } totalTokens)
            {
                return Unavailable("ccusage reported an active block with no token counts.");
            }

            // No denominator means no percentage. Inventing one would silently gate runs on a
            // number that means nothing, which is worse than admitting we do not know
            // (plan.md §4.2, §11.2).
            if (TryReadTokenLimit(block, root) is not { } tokenLimit || tokenLimit <= 0)
            {
                return Unavailable(
                    "ccusage did not report a token limit — check the command still passes " +
                    "`--token-limit max` without `--active`, which suppresses it.");
            }

            percent = Math.Clamp(totalTokens / tokenLimit * 100d, 0d, 100d);
            _logger.LogDebug(
                "ccusage: {Total} of {Limit} tokens in the active block ({Percent:0.0}%).",
                totalTokens, tokenLimit, percent);
        }

        // ccusage only ever measures the rolling 5-hour block, so the weekly windows stay null and
        // `HighestOfAll` degrades to the one window we actually know (plan.md §4.3).
        var window = new UsageWindow(percent, TryReadResetsAt(block));

        return new UsageSnapshot(
            window,
            SevenDay: null,
            SevenDayOpus: null,
            SevenDaySonnet: null,
            UsageSource.Ccusage,
            IsApproximate: true,
            _timeProvider.GetUtcNow());
    }

    /// <summary>The configured override if there is one, else <see cref="DefaultCommand"/>.</summary>
    static CcusageCommand ResolveCommand(PilotSettings settings)
    {
        var line = settings.CcusageCommandOverride;
        if (string.IsNullOrWhiteSpace(line) || SplitCommandLine(line) is not { Count: > 0 } tokens)
        {
            tokens = SplitCommandLine(DefaultCommand);
        }

        return new CcusageCommand(tokens[0], tokens.Skip(1).ToArray());
    }

    /// <summary>
    /// Splits a command line on whitespace, honouring double quotes so an override can name a
    /// program under `C:\Program Files\...`. Backslashes are left alone — they are path separators
    /// far more often than escapes on the platform this ships to.
    /// </summary>
    static List<string> SplitCommandLine(string line)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        foreach (var c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    /// <summary>
    /// Trims anything around the JSON payload. `npx` is entitled to print "Need to install the
    /// following packages" and progress noise on the same stream as the result.
    /// </summary>
    static bool TryExtractJson(string output, out string json)
    {
        json = string.Empty;
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        var start = output.IndexOfAny(['{', '[']);
        if (start < 0)
        {
            return false;
        }

        var end = output.LastIndexOfAny(['}', ']']);
        if (end <= start)
        {
            return false;
        }

        json = output[start..(end + 1)];
        return true;
    }

    /// <summary>
    /// plan.md §4.2's prescribed strategy: find the first array whose elements include an object
    /// flagged <c>isActive</c>, whatever the property that array hangs off is called. This is what
    /// makes both the <c>blocks</c> and the <c>data</c> envelopes work without sniffing for either.
    /// </summary>
    static JsonObject? FindActiveBlock(JsonNode? node, int depth)
    {
        if (node is null || depth > MaxSearchDepth)
        {
            return null;
        }

        if (node is JsonArray array)
        {
            foreach (var element in array)
            {
                if (element is JsonObject candidate && ReadBoolean(candidate["isActive"]) == true)
                {
                    return candidate;
                }
            }

            foreach (var element in array)
            {
                if (FindActiveBlock(element, depth + 1) is { } nested)
                {
                    return nested;
                }
            }

            return null;
        }

        if (node is JsonObject obj)
        {
            foreach (var (_, child) in obj)
            {
                if (FindActiveBlock(child, depth + 1) is { } nested)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// An explicit total wins; otherwise the components are summed. Both are tried flat on the
    /// block and nested under <c>tokenCounts</c>, because ccusage has shipped both layouts.
    /// </summary>
    static double? TryReadTotalTokens(JsonObject block)
    {
        var nested = block["tokenCounts"] as JsonObject;

        foreach (var source in Sources(block, nested))
        {
            if (ReadNumber(source["totalTokens"]) is { } total && total > 0)
            {
                return total;
            }
        }

        foreach (var source in Sources(block, nested))
        {
            var sum =
                (ReadNumber(source["inputTokens"]) ?? 0) +
                (ReadNumber(source["outputTokens"]) ?? 0) +
                (ReadNumber(source["cacheCreationInputTokens"]) ?? ReadNumber(source["cacheCreationTokens"]) ?? 0) +
                (ReadNumber(source["cacheReadInputTokens"]) ?? ReadNumber(source["cacheReadTokens"]) ?? 0);

            if (sum > 0)
            {
                return sum;
            }
        }

        return null;
    }

    /// <summary>
    /// The percentage ccusage worked out for itself. Present (as <c>tokenLimitStatus.percentUsed</c>,
    /// verified on 20.0.18) whenever <c>--token-limit</c> resolved to something; absent entirely
    /// when it did not, in which case the caller falls back to tokens ÷ limit.
    /// </summary>
    static double? TryReadPercentUsed(JsonObject block, JsonNode root)
    {
        var candidates = new[]
        {
            (block["tokenLimitStatus"] as JsonObject)?["percentUsed"],
            block["percentUsed"],
            ((root as JsonObject)?["tokenLimitStatus"] as JsonObject)?["percentUsed"],
            (root as JsonObject)?["percentUsed"],
            (((root as JsonObject)?["summary"] as JsonObject)?["tokenLimitStatus"] as JsonObject)?["percentUsed"],
        };

        foreach (var candidate in candidates)
        {
            // 0% is a real reading (a brand-new block), so only a missing value falls through.
            if (ReadNumber(candidate) is { } percent && percent >= 0)
            {
                return percent;
            }
        }

        return null;
    }

    /// <summary>
    /// The ceiling <c>--token-limit</c> resolved to. Newer ccusage puts it on the block under
    /// <c>tokenLimitStatus</c>; older output only has it at the top level or in <c>summary</c>.
    /// </summary>
    static double? TryReadTokenLimit(JsonObject block, JsonNode root)
    {
        var candidates = new[]
        {
            (block["tokenLimitStatus"] as JsonObject)?["limit"],
            block["tokenLimit"],
            (root as JsonObject)?["tokenLimit"],
            ((root as JsonObject)?["tokenLimitStatus"] as JsonObject)?["limit"],
            ((root as JsonObject)?["summary"] as JsonObject)?["tokenLimit"],
            (((root as JsonObject)?["summary"] as JsonObject)?["tokenLimitStatus"] as JsonObject)?["limit"],
        };

        foreach (var candidate in candidates)
        {
            if (ReadNumber(candidate) is { } limit && limit > 0)
            {
                return limit;
            }
        }

        return null;
    }

    /// <summary>When the active block rolls over, so the scheduler can resume promptly (plan.md §6).</summary>
    static DateTimeOffset? TryReadResetsAt(JsonObject block)
    {
        foreach (var name in (string[])["endTime", "endsAt", "resetsAt", "resetTime"])
        {
            if (block[name] is JsonValue value &&
                value.TryGetValue<string>(out var text) &&
                DateTimeOffset.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    static IEnumerable<JsonObject> Sources(JsonObject block, JsonObject? nested)
    {
        if (nested is not null)
        {
            yield return nested;
        }

        yield return block;
    }

    /// <summary>Numbers have also shipped as JSON strings; accept either rather than losing the reading.</summary>
    static double? ReadNumber(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<double>(out var number))
        {
            return number;
        }

        return value.TryGetValue<string>(out var text) &&
               double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    static bool? ReadBoolean(JsonNode? node)
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

    static string FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var trimmed = text.Trim();
        var newline = trimmed.IndexOf('\n');
        return newline < 0 ? trimmed : trimmed[..newline].TrimEnd();
    }

    static string Detail(string text)
    {
        var trimmed = FirstLine(text);
        return trimmed.Length <= MaxReasonDetailLength
            ? trimmed
            : trimmed[..MaxReasonDetailLength] + "…";
    }

    UsageSnapshot Unavailable(string reason) =>
        UsageSnapshot.Unavailable(reason, _timeProvider.GetUtcNow());
}
