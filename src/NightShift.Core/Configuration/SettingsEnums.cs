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
