using System.Text.Json.Serialization;
using NightShift.Core.Configuration;
using NightShift.Core.Usage;

namespace NightShift.Core.History;

/// <summary>How a cycle ended (plan.md §8).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RunOutcome>))]
public enum RunOutcome
{
    Success,
    Failed,
    TimedOut,
    Skipped,

    /// <summary>Visible-terminal mode: the process was started but its result is unobservable.</summary>
    Launched,

    /// <summary>
    /// The run was cut short by the subscription's quota, partway through its work. Distinct from
    /// <see cref="Failed"/> because nothing is wrong — the pilot simply has to wait for the window
    /// to roll over, and should resume the same session when it does.
    /// </summary>
    RateLimited,
}

/// <summary>Why a cycle did not launch Claude (plan.md §6).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SkipReason>))]
public enum SkipReason
{
    None,
    Disabled,
    AlreadyRunning,
    PreflightFailed,
    OverThreshold,
    UsageUnavailable,
    DryRun,

    /// <summary>
    /// The plan has nothing left to work on — every checkbox ticked, or every milestone delivered.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="PreflightFailed"/> because a finished plan is not a failure. It is
    /// its own reason so the History tab can say "nothing to do" rather than "blocked", which are
    /// very different things to read at breakfast.
    /// </remarks>
    NoWorkLeft,
}

/// <summary>
/// One line of <c>runs/index.jsonl</c>. Settable properties for the same source-generator reason
/// documented on <see cref="PilotSettings"/>; treat instances as immutable.
/// </summary>
public sealed record RunRecord
{
    /// <summary><see cref="Summary"/> is truncated to this many characters (plan.md §8).</summary>
    public const int MaxSummaryLength = 500;

    public string Id { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public RunOutcome Outcome { get; set; }

    public SkipReason SkipReason { get; set; }

    /// <summary>Human-readable detail for <see cref="SkipReason"/>, e.g. the failing preflight check.</summary>
    public string? SkipDetail { get; set; }

    /// <summary>The whole usage snapshot the decision was made from. Null for skips that never got that far.</summary>
    public UsageSnapshot? UsageAtStart { get; set; }

    public string? SessionId { get; set; }

    public double? CostUsd { get; set; }

    public int? ExitCode { get; set; }

    /// <summary>
    /// Which `--permission-mode` the run actually used — the fallback ladder in plan.md §5.3.2 means
    /// this is not always the configured one (§11.4c).
    /// </summary>
    public PermissionMode? PermissionModeUsed { get; set; }

    public string? TranscriptPath { get; set; }

    /// <summary>
    /// When the exhausted quota window rolls over, for <see cref="RunOutcome.RateLimited"/> runs.
    /// The scheduler waits for this rather than burning cycles against a quota that has not returned.
    /// </summary>
    public DateTimeOffset? RateLimitResetsAt { get; set; }

    /// <summary>
    /// Set when the run stopped mid-task and its session can be continued. The next cycle resumes
    /// this session instead of starting cold, so partly-finished work is picked up where it stopped.
    /// </summary>
    public bool IsResumable { get; set; }

    /// <summary>Last assistant text, truncated. Set via <see cref="WithSummary"/>.</summary>
    public string? Summary { get; set; }

    public TimeSpan? Duration => EndedAt is { } ended ? ended - StartedAt : null;

    /// <summary>Returns a copy whose <see cref="Summary"/> is trimmed to <see cref="MaxSummaryLength"/>.</summary>
    public RunRecord WithSummary(string? summary) => this with
    {
        Summary = summary is { Length: > MaxSummaryLength }
            ? summary[..MaxSummaryLength]
            : summary,
    };

    /// <summary>A new record for a run starting now. Ids are sortable so the index reads chronologically.</summary>
    public static RunRecord Start(DateTimeOffset startedAt) => new()
    {
        Id = $"{startedAt.UtcDateTime:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("n")[..6]}",
        StartedAt = startedAt,
    };
}
