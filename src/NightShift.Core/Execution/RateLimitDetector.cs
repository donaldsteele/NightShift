using System.Text.RegularExpressions;

namespace NightShift.Core.Execution;

/// <summary>Where a rate-limit conclusion came from, strongest first.</summary>
public enum RateLimitSignal
{
    /// <summary><c>rate_limit_event</c> said the request was not allowed.</summary>
    RateLimitEvent,

    /// <summary>The final <c>result</c> carried HTTP 429.</summary>
    ApiErrorStatus,

    /// <summary>The final <c>result</c> text was Claude Code's usage-limit message.</summary>
    ResultText,

    /// <summary>stderr carried the usage-limit message.</summary>
    StandardError,
}

/// <summary>Evidence that a run was cut short by the subscription's quota.</summary>
/// <param name="Signal">Which signal fired.</param>
/// <param name="RateLimitType">e.g. <c>five_hour</c>, <c>seven_day</c>, when reported.</param>
/// <param name="ResetsAt">When the exhausted window rolls over, when reported.</param>
/// <param name="Detail">Human-readable explanation for the history row and the status pill.</param>
public sealed record RateLimitEncounter(
    RateLimitSignal Signal,
    string? RateLimitType,
    DateTimeOffset? ResetsAt,
    string Detail);

/// <summary>
/// Watches a run for the moment Claude Code runs out of subscription quota mid-task (plan.md §6).
/// </summary>
/// <remarks>
/// <para>
/// This is the failure mode that a naive pilot handles worst: the run stops partway through an
/// item, the exit looks ordinary, and the scheduler cheerfully tries again an hour later against a
/// quota that has not come back yet.
/// </para>
/// <para>
/// <b>False positives are a real hazard here, not a theoretical one.</b> NightShift's own plan.md
/// discusses rate limits on nearly every page, so a run of NightShift against itself will produce
/// assistant prose and commit messages full of the words "rate limit". Matching a bare "rate limit"
/// substring would mark healthy runs as quota-blocked and stall the pilot for hours. Detection
/// therefore only trusts:
/// </para>
/// <list type="number">
/// <item>structured <c>rate_limit_event</c> status/utilization,</item>
/// <item><c>api_error_status</c> of 429 on the final result,</item>
/// <item>Claude Code's own usage-limit sentence, anchored — not any mention of the topic.</item>
/// </list>
/// Assistant message text is deliberately never inspected: it is the model talking, not the CLI
/// reporting.
/// </remarks>
public sealed partial class RateLimitDetector
{
    /// <summary>`status` values that mean the request went through.</summary>
    static readonly HashSet<string> AllowedStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "allowed", "allowed_warning", "ok", "allowed_overage" };

    /// <summary>
    /// Claude Code's usage-limit message. Anchored to the whole sentence rather than the phrase, so
    /// a task that merely talks about limits cannot trip it.
    /// </summary>
    [GeneratedRegex(
        @"(claude\s+)?(usage|rate)\s+limit\s+reached|usage\s+limit\s+will\s+reset|you'?ve\s+(hit|reached)\s+your\s+(usage|rate)\s+limit",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UsageLimitMessage();

    /// <summary>`Your limit will reset at 3pm (America/New_York)` and similar.</summary>
    [GeneratedRegex(
        @"reset(s)?\s+at\s+(?<when>[0-9]{1,2}(:[0-9]{2})?\s*(am|pm)?[^.\n]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResetPhrase();

    /// <summary>The strongest encounter seen so far, or null if the run was never quota-blocked.</summary>
    public RateLimitEncounter? Encounter { get; private set; }

    /// <summary>True when the run was cut short by quota.</summary>
    public bool WasRateLimited => Encounter is not null;

    /// <summary>
    /// The last reset time reported by any <c>rate_limit_event</c>, even a healthy one. Useful for
    /// anchoring the schedule regardless of how the run ended.
    /// </summary>
    public DateTimeOffset? LastKnownResetsAt { get; private set; }

    public void Observe(ClaudeStreamEvent streamEvent)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);

        switch (streamEvent)
        {
            case ClaudeRateLimitEvent rateLimit:
                ObserveRateLimit(rateLimit);
                break;

            case ClaudeResultEvent result:
                ObserveResult(result);
                break;
        }
    }

    void ObserveRateLimit(ClaudeRateLimitEvent rateLimit)
    {
        if (rateLimit.ResetsAt is { } resetsAt)
        {
            LastKnownResetsAt = resetsAt;
        }

        var statusBlocks = rateLimit.Status is { Length: > 0 } status && !AllowedStatuses.Contains(status);
        var exhausted = rateLimit.UtilizationPercent >= 100;

        if (!statusBlocks && !exhausted)
        {
            return;
        }

        var detail = statusBlocks
            ? $"Claude reported rate-limit status '{rateLimit.Status}' for the {rateLimit.RateLimitType ?? "usage"} window."
            : $"The {rateLimit.RateLimitType ?? "usage"} window reached {rateLimit.UtilizationPercent:0.##}%.";

        Promote(new RateLimitEncounter(
            RateLimitSignal.RateLimitEvent,
            rateLimit.RateLimitType,
            rateLimit.ResetsAt,
            detail));
    }

    void ObserveResult(ClaudeResultEvent result)
    {
        // Arrives as a string; compare textually rather than assuming it parses.
        if (result.ApiErrorStatus is { Length: > 0 } apiStatus
            && apiStatus.Contains("429", StringComparison.Ordinal))
        {
            Promote(new RateLimitEncounter(
                RateLimitSignal.ApiErrorStatus,
                null,
                LastKnownResetsAt,
                "The run ended with HTTP 429 from the API."));
            return;
        }

        if (result.ResultText is { Length: > 0 } text && UsageLimitMessage().IsMatch(text))
        {
            Promote(new RateLimitEncounter(
                RateLimitSignal.ResultText,
                null,
                LastKnownResetsAt,
                $"Claude Code reported a usage limit: {Trim(text)}"));
        }
    }

    /// <summary>stderr is inspected because a hard limit can end a run before any result arrives.</summary>
    public void ObserveStandardError(string? line)
    {
        if (line is not { Length: > 0 } || !UsageLimitMessage().IsMatch(line))
        {
            return;
        }

        Promote(new RateLimitEncounter(
            RateLimitSignal.StandardError,
            null,
            LastKnownResetsAt,
            $"Claude Code reported a usage limit on stderr: {Trim(line)}"));
    }

    /// <summary>Keeps the strongest signal, and fills in a reset time from a weaker one if needed.</summary>
    void Promote(RateLimitEncounter encounter)
    {
        var resetsAt = encounter.ResetsAt ?? LastKnownResetsAt;
        var candidate = encounter with { ResetsAt = resetsAt };

        if (Encounter is null || candidate.Signal < Encounter.Signal)
        {
            Encounter = candidate;
        }
        else if (Encounter.ResetsAt is null && resetsAt is not null)
        {
            Encounter = Encounter with { ResetsAt = resetsAt };
        }
    }

    /// <summary>
    /// Best-effort human-readable reset hint from prose, for when no structured time was reported.
    /// Deliberately not parsed into a <see cref="DateTimeOffset"/> — the message carries a wall
    /// clock in an unstated timezone, and guessing would produce a schedule that is confidently
    /// wrong.
    /// </summary>
    public static string? ExtractResetHint(string? text) =>
        text is { Length: > 0 } && ResetPhrase().Match(text) is { Success: true } match
            ? match.Groups["when"].Value.Trim()
            : null;

    static string Trim(string text) =>
        text.Length <= 200 ? text.Trim() : text[..200].Trim() + "…";
}
