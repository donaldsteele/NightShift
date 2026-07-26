using ClaudePilot.Core.Usage;

namespace ClaudePilot.Core.Configuration;

/// <summary>
/// Every user-facing option, matching the Settings screen in plan.md §9.2. Defaults here are the
/// defaults the UI shows; nothing else may invent its own.
/// </summary>
/// <remarks>
/// Properties are <c>get; set;</c> rather than <c>get; init;</c> deliberately. The System.Text.Json
/// source generator compiles <c>init</c> properties into a parameterized-construction path that
/// passes <c>default(T)</c> for every property absent from the JSON, wiping the defaults below
/// whenever an older or hand-trimmed settings file is read. Settable properties make the generator
/// emit a plain <c>new PilotSettings()</c> and assign only what the file actually contains.
/// Treat instances as immutable anyway: mutate with <c>with</c>, never in place.
/// </remarks>
public sealed record PilotSettings
{
    /// <summary>Bumped whenever a migration is needed. See <see cref="JsonSettingsStore"/>.</summary>
    public const int CurrentVersion = 1;

    public const int MinIntervalMinutes = 5;
    public const int MaxIntervalMinutes = 1440;
    public const double MinThresholdPercent = 50;
    public const double MaxThresholdPercent = 100;

    /// <summary>
    /// The prompt body sent to Claude, per plan.md §5.2. The `/caveman &lt;level&gt;` line is *not*
    /// part of it — <c>PromptBuilder</c> prepends that from <see cref="CavemanLevel"/> so the two
    /// settings stay independent. <c>{planFile}</c> is substituted with <see cref="PlanFileName"/>.
    /// </summary>
    public const string DefaultPromptTemplate = """
        Continue work on this project.

        Read `{planFile}` in this directory. It is the authoritative task list.
        Pick up from the first unchecked item and make concrete progress.

        Rules for this session:
        - You are running unattended. There is no human available to answer questions.
          Never ask for confirmation, clarification, or approval — decide and proceed.
        - Work only on items in {planFile}. Do not invent new scope.
        - After finishing an item, tick its checkbox in {planFile} and commit that change
          along with the code, using a conventional commit message.
        - If an item is ambiguous or blocked, mark it `- [!]` in {planFile} with a one-line
          note explaining the blocker, then move to the next item.
        - Prefer finishing one item completely over starting several.
        - Run the project's tests before you finish. If they fail, fix them.
        - End your run in a clean state: no half-applied edits, everything committed.
        """;

    public const string DefaultAllowedTools = "Bash,Read,Edit,Write,Glob,Grep";

    public int SettingsVersion { get; set; } = CurrentVersion;

    // ── Project ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Directory the pilot babysits. Empty until the user picks one.</summary>
    public string ProjectDirectory { get; set; } = string.Empty;

    public string PlanFileName { get; set; } = "plan.md";

    // ── Schedule ───────────────────────────────────────────────────────────────────────────────

    public int IntervalMinutes { get; set; } = 60;

    public bool RunOnStartup { get; set; }

    public bool StartWithWindows { get; set; }

    public bool StartMinimized { get; set; }

    // ── Usage gate ─────────────────────────────────────────────────────────────────────────────

    public double ThresholdPercent { get; set; } = 90;

    public UsageMetric UsageMetric { get; set; } = UsageMetric.HighestOfAll;

    public UsageUnavailableBehavior OnUsageUnavailable { get; set; } = UsageUnavailableBehavior.Skip;

    // ── Claude ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Explicit path to the CLI. Empty means "auto-detect" (plan.md §5.1).</summary>
    public string ClaudeExecutablePath { get; set; } = string.Empty;

    public LaunchMode LaunchMode { get; set; } = LaunchMode.Headless;

    /// <summary>Null or empty means "don't pass `--model`, let Claude Code decide".</summary>
    public string? Model { get; set; }

    public ResumeStrategy ResumeStrategy { get; set; } = ResumeStrategy.Fresh;

    /// <summary>Default 55 so a run cannot outlive a 60-minute slot.</summary>
    public int MaxRunDurationMinutes { get; set; } = 55;

    /// <summary>No stream event for this long ⇒ assume a hidden prompt or hung tool (plan.md §5.3.3).</summary>
    public int StallTimeoutMinutes { get; set; } = 10;

    // ── Autonomy ───────────────────────────────────────────────────────────────────────────────

    public PermissionMode PermissionMode { get; set; } = PermissionMode.Auto;

    /// <summary>Write the trust keys into `~/.claude.json` before a run (plan.md §5.3.1).</summary>
    public bool AutoTrustProjectFolder { get; set; } = true;

    public string AllowedTools { get; set; } = DefaultAllowedTools;

    /// <summary>
    /// Last-resort trust bypass: launch with `--dangerously-skip-permissions`. Off by default;
    /// it disables all permission checking, not just the trust dialog.
    /// </summary>
    public bool SkipPermissionsFallback { get; set; }

    // ── Prompt ─────────────────────────────────────────────────────────────────────────────────

    public CavemanLevel CavemanLevel { get; set; } = CavemanLevel.Full;

    public string PromptTemplate { get; set; } = DefaultPromptTemplate;

    // ── Advanced ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Provider preference order for <c>CompositeUsageProvider</c> (plan.md §4.3).</summary>
    public UsageProviderPreference UsageProviderOrder { get; set; } =
        UsageProviderPreference.OAuthThenCcusage;

    /// <summary>Overrides the default `npx ccusage@latest ...` invocation. Empty means default.</summary>
    public string CcusageCommandOverride { get; set; } = string.Empty;

    /// <summary>Run the whole cycle and log the resolved command line, but never spawn Claude.</summary>
    public bool DryRun { get; set; }

    public int HistoryRetentionCount { get; set; } = 200;

    /// <summary>
    /// Forces every value back into its documented range. Applied on load and before save so a
    /// hand-edited settings file can never put the scheduler into a nonsense state.
    /// </summary>
    public PilotSettings Normalized() => this with
    {
        PlanFileName = string.IsNullOrWhiteSpace(PlanFileName) ? "plan.md" : PlanFileName.Trim(),
        IntervalMinutes = Math.Clamp(IntervalMinutes, MinIntervalMinutes, MaxIntervalMinutes),
        ThresholdPercent = Math.Clamp(ThresholdPercent, MinThresholdPercent, MaxThresholdPercent),
        MaxRunDurationMinutes = Math.Max(1, MaxRunDurationMinutes),
        StallTimeoutMinutes = Math.Max(1, StallTimeoutMinutes),
        AllowedTools = string.IsNullOrWhiteSpace(AllowedTools) ? DefaultAllowedTools : AllowedTools.Trim(),
        PromptTemplate = string.IsNullOrWhiteSpace(PromptTemplate) ? DefaultPromptTemplate : PromptTemplate,
        HistoryRetentionCount = Math.Max(1, HistoryRetentionCount),
        Model = string.IsNullOrWhiteSpace(Model) ? null : Model.Trim(),
    };
}
