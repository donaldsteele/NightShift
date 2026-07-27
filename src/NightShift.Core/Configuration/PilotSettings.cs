using NightShift.Core.Usage;

namespace NightShift.Core.Configuration;

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
    /// <remarks>
    /// Version 2 introduced <c>{planConventions}</c> in <see cref="DefaultPromptTemplate"/>; a stored
    /// template still equal to <see cref="LegacyPromptTemplateV1"/> is replaced on load.
    /// </remarks>
    public const int CurrentVersion = 2;

    public const int MinIntervalMinutes = 5;
    public const int MaxIntervalMinutes = 1440;
    public const double MinThresholdPercent = 50;
    public const double MaxThresholdPercent = 100;

    /// <summary>
    /// The prompt body sent to Claude, per plan.md §5.2. The `/caveman &lt;level&gt;` line is *not*
    /// part of it — <c>PromptBuilder</c> prepends that from <see cref="CavemanLevel"/> so the two
    /// settings stay independent. <c>{planFile}</c> is substituted with <see cref="PlanFileName"/>,
    /// and <c>{planConventions}</c> with the rules for the plan's <see cref="PlanFormat"/>.
    /// </summary>
    public const string DefaultPromptTemplate = """
        Continue work on this project.

        Read `{planFile}` in this directory. It is the authoritative task list.

        {planConventions}

        Rules for this session:
        - You are running unattended. There is no human available to answer questions.
          Never ask for confirmation, clarification, or approval — decide and proceed.
        - Work only on what {planFile} already describes. Do not invent new scope.
        - Commit your work, using a conventional commit message.
        - Run the project's tests before you finish. If they fail, fix them.
        - End your run in a clean state: no half-applied edits, everything committed.
        """;

    /// <summary>
    /// The template as it shipped before <c>{planConventions}</c> existed.
    /// </summary>
    /// <remarks>
    /// Kept verbatim so <see cref="JsonSettingsStore"/> can recognise a stored template the user
    /// never edited and upgrade it. A template that differs from this by so much as a space is
    /// treated as hand-written and left exactly alone.
    /// </remarks>
    public const string LegacyPromptTemplateV1 = """
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

    /// <summary>
    /// Which convention <see cref="PlanFileName"/> uses. Detected by default (plan.md §9.1).
    /// </summary>
    /// <remarks>
    /// Decides both the project card's tally and which conventions the prompt tells a run to follow,
    /// so getting it wrong is not cosmetic: a milestone plan parsed as checkboxes reports "0 of 0",
    /// and a run told to tick a checkbox that does not exist can never mark its work done.
    /// </remarks>
    public PlanFormat PlanFormat { get; set; } = PlanFormat.Auto;

    // ── Schedule ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Minutes between checks, and an upper bound rather than a metronome (plan.md §6).
    /// </summary>
    /// <remarks>
    /// Five, not an hour: a window that rolls over is only useful until it fills again, and an hourly
    /// tick can leave most of a freed session window unspent. A check that decides to skip costs one
    /// cached usage lookup, and <c>RunGate</c> makes a tick that lands during a run skip rather than
    /// queue, so the frequency is close to free.
    /// </remarks>
    public int IntervalMinutes { get; set; } = 5;

    public bool RunOnStartup { get; set; }

    /// <summary>
    /// Anchor the schedule to quota resets instead of ticking blindly on the interval (plan.md §6).
    /// </summary>
    /// <remarks>
    /// When the usage provider reports when a window rolls over, the next check is pulled forward to
    /// just after it. A reset is the moment the most quota becomes available, so a tick placed there
    /// gets the most work done per slot — and when a cycle was blocked by the threshold, it is the
    /// first moment the pilot can run at all. The interval remains the upper bound: alignment only
    /// ever moves a check <em>earlier</em>, never later.
    /// </remarks>
    public bool AlignToQuotaReset { get; set; } = true;

    /// <summary>
    /// How long after a reset to wake. Small but non-zero: firing at the exact boundary races the
    /// server's own clock and reads the window that is about to close.
    /// </summary>
    public int QuotaResetGraceMinutes { get; set; } = 1;

    /// <summary>
    /// Longest the pilot will sleep waiting for exhausted quota before re-checking anyway.
    /// </summary>
    /// <remarks>
    /// <b>Do not remove this cap.</b> <c>seven_day.resets_at</c> is not when the quota comes back —
    /// it reports when the oldest tokens age out, roughly seven days ahead, while the counter
    /// actually resets on a ~72-hour cycle (independently measured at 71.9h, 72.6h and 72.5h over
    /// three consecutive cycles, and separately observed dropping 60% → 2% while <c>resets_at</c>
    /// still claimed nine hours away). Trusting that timestamp literally would park an unattended
    /// pilot for up to a week. A usage check costs one cached HTTP call, so re-checking is strictly
    /// cheaper than being wrong.
    /// </remarks>
    public int MaxQuotaWaitHours { get; set; } = 6;

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

    /// <summary>
    /// Start the launched session with Claude Code's Remote Control enabled, named after the
    /// repository, so it can be driven from another device.
    /// </summary>
    /// <remarks>
    /// <b>Visible-terminal mode only.</b> Measured against Claude Code 2.1.220 on 2026-07-27:
    /// <c>--remote-control</c> is accepted in headless <c>-p</c> mode and then **silently ignored** —
    /// exit 0, no error, and no remote-control field anywhere in the <c>system/init</c> event. The
    /// flag's own help says it "starts an interactive session", and an interactive session needs a
    /// real terminal; without a tty the CLI falls back to <c>--print</c> and refuses to start.
    /// <para>
    /// So NightShift does not pass the flag to headless runs at all. Passing a flag that looks like
    /// it worked and did nothing is the same failure that made <c>/caveman</c> swallow a whole
    /// prompt (§5.2). Preflight raises a warning instead when this is on and
    /// <see cref="LaunchMode"/> is <see cref="LaunchMode.Headless"/>.
    /// </para>
    /// </remarks>
    public bool EnableRemoteControl { get; set; }

    /// <summary>
    /// Overrides the Remote Control session name. Empty means derive it from the repository — the
    /// <c>origin</c> remote's name, or the project directory name (see <c>RepositoryName</c>).
    /// </summary>
    public string RemoteControlName { get; set; } = string.Empty;

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
        QuotaResetGraceMinutes = Math.Clamp(QuotaResetGraceMinutes, 0, 60),
        MaxQuotaWaitHours = Math.Clamp(MaxQuotaWaitHours, 1, 72),
        AllowedTools = string.IsNullOrWhiteSpace(AllowedTools) ? DefaultAllowedTools : AllowedTools.Trim(),
        RemoteControlName = RemoteControlName?.Trim() ?? string.Empty,
        PromptTemplate = string.IsNullOrWhiteSpace(PromptTemplate) ? DefaultPromptTemplate : PromptTemplate,
        HistoryRetentionCount = Math.Max(1, HistoryRetentionCount),
        Model = string.IsNullOrWhiteSpace(Model) ? null : Model.Trim(),
    };
}
