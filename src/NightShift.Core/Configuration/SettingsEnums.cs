using System.Text.Json.Serialization;

namespace NightShift.Core.Configuration;

/// <summary>Which usage window the threshold is compared against (plan.md §4.3).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UsageMetric>))]
public enum UsageMetric
{
    /// <summary>The 5-hour session window only.</summary>
    FiveHour,

    /// <summary>The 7-day window only.</summary>
    SevenDay,

    /// <summary>
    /// The largest of every reported window. Default: it will not start a run that immediately
    /// burns the weekly cap.
    /// </summary>
    HighestOfAll,
}

/// <summary>
/// How the plan file states what is left to do (plan.md §9.1).
/// </summary>
/// <remarks>
/// Two conventions exist in the wild and neither can read the other. A checkbox plan is a flat list
/// of <c>- [ ]</c> task items; a milestone plan is a sequence of <c>### M7 — Title</c> headings, each
/// a body of work marked delivered in its own heading. Parsing a milestone plan with the checkbox
/// counter reports "0 of 0", and telling a run to "tick its checkbox" in one is an instruction it
/// cannot carry out — so the format decides both the tally and the prompt.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<PlanFormat>))]
public enum PlanFormat
{
    /// <summary>
    /// Work it out from the file. Default: any checkbox wins, otherwise any milestone heading, and a
    /// file with neither is treated as an empty checkbox plan — which is what it looks like.
    /// </summary>
    Auto,

    /// <summary><c>- [ ]</c> / <c>- [x]</c> / <c>- [!]</c> task-list items.</summary>
    Checkbox,

    /// <summary><c>### M7 — Title</c> headings, delivered ones marked in the heading or a status line.</summary>
    Milestone,
}

/// <summary>What to do when no usage figure could be obtained (plan.md §4.3).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UsageUnavailableBehavior>))]
public enum UsageUnavailableBehavior
{
    /// <summary>Skip the cycle. Default — silently burning quota on a broken scrape is worse.</summary>
    Skip,

    /// <summary>Run anyway.</summary>
    Run,
}

/// <summary>How Claude Code is launched (plan.md §5.4, §5.5).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LaunchMode>))]
public enum LaunchMode
{
    /// <summary>`claude -p` with stream-json output captured into the run log.</summary>
    Headless,

    /// <summary>A visible interactive terminal; no output capture.</summary>
    VisibleTerminal,
}

/// <summary>Whether each run continues the previous Claude session (plan.md §5.4).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ResumeStrategy>))]
public enum ResumeStrategy
{
    /// <summary>New session every run. Default — keeps context small; plan.md carries continuity.</summary>
    Fresh,

    /// <summary>Pass `--resume &lt;sessionId&gt;`.</summary>
    Resume,
}

/// <summary>
/// The `--permission-mode` value a run is launched with (plan.md §5.3.2). `plan` is deliberately
/// absent: it ends by waiting for a human, which is exactly what this app must never do.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PermissionMode>))]
public enum PermissionMode
{
    /// <summary>Everything runs, a classifier blocks destructive actions. The default.</summary>
    Auto,

    /// <summary>Auto-approves edits only; needs a broad `--allowedTools` list. Fallback.</summary>
    AcceptEdits,

    /// <summary>No permission checking and no classifier. Explicit opt-in, isolated repos only.</summary>
    BypassPermissions,
}

/// <summary>Levels accepted by the third-party caveman skill (plan.md §5.2).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CavemanLevel>))]
public enum CavemanLevel
{
    Lite,
    Full,
    Ultra,
    WenyanLite,
    WenyanFull,
    WenyanUltra,
}

/// <summary>
/// The plain-language name of a <see cref="UsageMetric"/>. Lives in Core because both the Settings
/// dropdown and the dashboard's gate line render it, and two tables would eventually disagree about
/// what the pilot is actually gating on.
/// </summary>
public static class UsageMetricText
{
    public static string Describe(UsageMetric metric) => metric switch
    {
        UsageMetric.HighestOfAll => "Highest of all",
        UsageMetric.FiveHour => "Session (5h)",
        UsageMetric.SevenDay => "Weekly (7d)",
        _ => metric.ToString(),
    };
}

public static class PermissionModeExtensions
{
    /// <summary>Maps to the exact string Claude Code's `--permission-mode` flag expects.</summary>
    public static string ToCliValue(this PermissionMode mode) => mode switch
    {
        PermissionMode.Auto => "auto",
        PermissionMode.AcceptEdits => "acceptEdits",
        PermissionMode.BypassPermissions => "bypassPermissions",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown permission mode"),
    };
}

public static class CavemanLevelExtensions
{
    /// <summary>Maps to the argument of the `/caveman` slash command.</summary>
    public static string ToCommandArgument(this CavemanLevel level) => level switch
    {
        CavemanLevel.Lite => "lite",
        CavemanLevel.Full => "full",
        CavemanLevel.Ultra => "ultra",
        CavemanLevel.WenyanLite => "wenyan-lite",
        CavemanLevel.WenyanFull => "wenyan-full",
        CavemanLevel.WenyanUltra => "wenyan-ultra",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown caveman level"),
    };
}
