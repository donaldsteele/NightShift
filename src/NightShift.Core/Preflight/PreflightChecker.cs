using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NightShift.Core.Configuration;
using NightShift.Core.Execution;
using NightShift.Core.Usage;

namespace NightShift.Core.Preflight;

/// <summary>The pill colour for one check (plan.md §9.1: red / amber / green).</summary>
public enum PreflightStatus
{
    /// <summary>Green. Nothing to do.</summary>
    Ok,

    /// <summary>Amber. The run will proceed, but degraded or unverified.</summary>
    Warning,

    /// <summary>Red. An unattended run started now would fail, hang, or do nothing.</summary>
    Error,
}

/// <summary>
/// Stable identity for a row in the plan.md §7 table. The UI keys pills off this rather than off the
/// display name, so wording can change without breaking a binding or a saved dismissal.
/// </summary>
public enum PreflightCheckId
{
    /// <summary>The Claude Code CLI could be located (plan.md §5.1).</summary>
    ClaudeExecutable,

    /// <summary><c>claude --version</c> actually starts and exits 0.</summary>
    ClaudeVersion,

    /// <summary><see cref="PilotSettings.ProjectDirectory"/> exists.</summary>
    ProjectDirectory,

    /// <summary><see cref="PilotSettings.PlanFileName"/> exists inside it.</summary>
    PlanFile,

    /// <summary>The plan still has unticked boxes worth spending a slot on.</summary>
    PlanItems,

    /// <summary>The OAuth credential is readable and unexpired.</summary>
    Credentials,

    /// <summary>The caveman plugin provides <c>/caveman:caveman</c> (plan.md §5.2 correction).</summary>
    CavemanPlugin,

    /// <summary>The project folder carries the trust flags (plan.md §5.3.1).</summary>
    WorkspaceTrust,

    /// <summary><c>--permission-mode auto</c> is usable, or the downgrade is explained.</summary>
    AutoMode,

    /// <summary>No <c>permissions.ask</c> rule or interactive MCP server can pause the run.</summary>
    PermissionsAsk,

    /// <summary>The project directory is a git repository.</summary>
    GitRepository,

    /// <summary>The working tree has no uncommitted changes.</summary>
    GitWorkingTree,

    /// <summary>
    /// Remote Control is switched on but the launch mode cannot deliver it (plan.md §5.5).
    /// </summary>
    RemoteControl,
}

/// <summary>
/// What the UI should offer to do about a failing check. This is <b>data, not behaviour</b>: Core has
/// no UI, so a fix can never be a delegate that opens a picker or a dialog. The app layer switches on
/// <see cref="PreflightFixAction.Command"/>, does whatever presentation it needs, and — for the
/// commands Core can carry out on its own — calls
/// <see cref="PreflightChecker.ApplyFixAsync(PreflightFixAction, PilotSettings, CancellationToken)"/>.
/// </summary>
public enum PreflightFixCommand
{
    /// <summary>App: file picker for <see cref="PilotSettings.ClaudeExecutablePath"/>.</summary>
    SetClaudeExecutablePath,

    /// <summary>App: folder picker for <see cref="PilotSettings.ProjectDirectory"/>.</summary>
    SetProjectDirectory,

    /// <summary>Core: writes a starter plan file at <see cref="PreflightFixAction.Target"/>.</summary>
    CreatePlanFile,

    /// <summary>App: opens the plan file so the user can add items.</summary>
    OpenPlanFile,

    /// <summary>App: shows the `claude` login instructions. Core never handles credentials for you.</summary>
    ShowLoginInstructions,

    /// <summary>Core: runs the two `claude plugin` commands from plan.md §5.2.</summary>
    InstallCavemanPlugin,

    /// <summary>Core: writes the trust keys via <see cref="WorkspaceTrustManager"/>.</summary>
    TrustProjectFolder,

    /// <summary>Core: re-runs the auto-mode probe, discarding the cached answer.</summary>
    ReprobeAutoMode,

    /// <summary>App: opens the settings file named by <see cref="PreflightFixAction.Target"/>.</summary>
    OpenSettingsFile,

    /// <summary>Core: runs `git init` in the project directory.</summary>
    InitializeGitRepository,
}

/// <summary>
/// An offer of a fix: an identifier the app can switch on, a label it can put on a button, and the
/// one piece of data the command needs (a path, usually).
/// </summary>
/// <param name="Label">Human wording for the button, e.g. "Trust this folder".</param>
/// <param name="Target">
/// The file or directory the command acts on, so the app never has to recompute it. Null for the
/// commands that need no argument.
/// </param>
public sealed record PreflightFixAction(PreflightFixCommand Command, string Label, string? Target = null)
{
    /// <summary>
    /// True when <see cref="PreflightChecker.ApplyFixAsync(PreflightFixAction, PilotSettings, CancellationToken)"/>
    /// can carry this out. False means the app owns it (a picker, a dialog, an editor) — Core will
    /// return <see cref="PreflightFixOutcome.HandledByTheApp"/> rather than pretend.
    /// </summary>
    public bool IsAppliedByCore => Command is
        PreflightFixCommand.CreatePlanFile or
        PreflightFixCommand.InstallCavemanPlugin or
        PreflightFixCommand.TrustProjectFolder or
        PreflightFixCommand.ReprobeAutoMode or
        PreflightFixCommand.InitializeGitRepository;
}

/// <summary>Outcome of <see cref="PreflightChecker.ApplyFixAsync"/>.</summary>
public enum PreflightFixOutcome
{
    /// <summary>Something was changed.</summary>
    Applied,

    /// <summary>Nothing needed changing — it was already in the desired state.</summary>
    AlreadyDone,

    /// <summary>The attempt failed; see the message. Never an exception.</summary>
    Failed,

    /// <summary>This command is the app's to perform; Core did nothing.</summary>
    HandledByTheApp,
}

/// <param name="Message">Safe to show verbatim; contains no secrets.</param>
public sealed record PreflightFixResult(
    PreflightFixCommand Command,
    PreflightFixOutcome Outcome,
    string Message)
{
    /// <summary>True when the underlying problem should now be gone. Re-run preflight to confirm.</summary>
    public bool Succeeded => Outcome is PreflightFixOutcome.Applied or PreflightFixOutcome.AlreadyDone;
}

/// <summary>
/// Checkbox tallies from the plan file, parsed the way plan.md §9.1 describes.
/// </summary>
/// <param name="Completed"><c>- [x]</c>.</param>
/// <param name="Remaining"><c>- [ ]</c> — the only ones a run can actually pick up.</param>
/// <param name="Blocked"><c>- [!]</c>, written by a previous run that hit a wall (plan.md §5.2).</param>
/// <remarks>
/// Carried on <see cref="PreflightResult"/> so the dashboard's project card can render
/// "12 of 30 items complete" straight from the preflight it already ran, instead of opening and
/// re-parsing the file itself.
/// </remarks>
public sealed record PlanItemCounts(int Completed, int Remaining, int Blocked)
{
    /// <summary>No checkboxes at all.</summary>
    public static PlanItemCounts Empty { get; } = new(0, 0, 0);

    /// <summary>Every checkbox found, whatever its mark.</summary>
    public int Total => Completed + Remaining + Blocked;

    /// <summary>True when there is at least one <c>- [ ]</c> left to work on.</summary>
    public bool HasRemainingWork => Remaining > 0;

    /// <summary>The plan.md §9.1 project-card string.</summary>
    public string Summary => $"{Completed} of {Total} items complete";
}

/// <summary>One row of the plan.md §7 table.</summary>
/// <param name="Name">Display name for the pill.</param>
/// <param name="Message">
/// One sentence, always populated — green rows say what was verified, so the strip reads as evidence
/// rather than as a row of unexplained ticks.
/// </param>
public sealed record PreflightCheck(
    PreflightCheckId Id,
    string Name,
    PreflightStatus Status,
    string Message)
{
    /// <summary>What to offer the user. Null when there is nothing sensible to offer.</summary>
    public PreflightFixAction? Fix { get; init; }

    /// <summary>
    /// Supporting lines for an expander: the probed paths, the ask rules, the dirty files. Kept out
    /// of <see cref="Message"/> because a pill tooltip wants one sentence and a diagnosis wants the
    /// list — "Claude Code not found on PATH" is useless without the paths that were tried.
    /// </summary>
    public IReadOnlyList<string> Details { get; init; } = [];

    /// <summary>True for anything that must be dealt with before an unattended run is worth starting.</summary>
    public bool IsBlocking => Status == PreflightStatus.Error;
}

/// <summary>
/// The whole preflight, as consumed by the scheduler and the dashboard (plan.md §7, §9.1).
/// </summary>
/// <param name="CheckedAt">When this ran, from the injected <see cref="TimeProvider"/>.</param>
/// <param name="PlanItems">Checkbox tallies, or null when the plan file could not be read.</param>
/// <param name="ClaudeExecutablePath">
/// The CLI that was found, so a caller that just ran preflight does not have to locate it again.
/// Null when the lookup failed.
/// </param>
/// <param name="PermissionMode">
/// The mode a run started now would really use, already decided by <see cref="AutoModeProbe"/>. Null
/// only when the decision itself failed.
/// </param>
public sealed record PreflightResult(
    IReadOnlyList<PreflightCheck> Checks,
    DateTimeOffset CheckedAt,
    PlanItemCounts? PlanItems = null,
    string? ClaudeExecutablePath = null,
    PermissionModeDecision? PermissionMode = null)
{
    /// <summary>The worst status present — the colour of the summary pill.</summary>
    public PreflightStatus Status => Checks.Count == 0
        ? PreflightStatus.Ok
        : Checks.Max(check => check.Status);

    /// <summary>True when at least one check is red.</summary>
    public bool HasErrors => Checks.Any(check => check.Status == PreflightStatus.Error);

    /// <summary>
    /// The scheduler's gate: no red rows. Amber rows never stop a run — an untrusted folder, a
    /// missing git repo and an auto-mode downgrade are all things an unattended run survives.
    /// </summary>
    public bool CanRun => !HasErrors;

    /// <summary>Red rows, in table order.</summary>
    public IEnumerable<PreflightCheck> Errors =>
        Checks.Where(check => check.Status == PreflightStatus.Error);

    /// <summary>Amber rows, in table order.</summary>
    public IEnumerable<PreflightCheck> Warnings =>
        Checks.Where(check => check.Status == PreflightStatus.Warning);

    /// <summary>The row for <paramref name="id"/>, or null if it was not produced.</summary>
    public PreflightCheck? Find(PreflightCheckId id) =>
        Checks.FirstOrDefault(check => check.Id == id);

    /// <summary>One line for a log or a tooltip, naming the blockers.</summary>
    public string Summary => HasErrors
        ? "Blocked: " + string.Join("; ", Errors.Select(check => $"{check.Name} — {check.Message}"))
        : Warnings.Any()
            ? "Ready, with warnings: " + string.Join("; ", Warnings.Select(check => check.Name))
            : "Ready.";
}

/// <summary>Raw outcome of one auxiliary process run (<c>claude --version</c>, <c>git status</c>, …).</summary>
/// <param name="Started">False when the executable could not be launched at all.</param>
public sealed record PreflightProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool Started = true,
    bool TimedOut = false)
{
    /// <summary>Started, did not time out, and exited 0.</summary>
    public bool Succeeded => Started && !TimedOut && ExitCode == 0;
}

/// <summary>
/// Runs the small helper processes preflight needs. Behind an interface for one reason: <b>no test in
/// this repo may spawn the real <c>claude</c></b> (or depend on the developer having git installed),
/// so every process the checker starts goes through here.
/// </summary>
public interface IPreflightProcessRunner
{
    /// <summary>
    /// Runs <paramref name="executable"/> to completion. Implementations must not throw for an
    /// executable that is missing or fails to start — that is an ordinary answer preflight reports,
    /// not an exception the scheduler has to catch.
    /// </summary>
    Task<PreflightProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>
/// The real runner. Stdin is redirected and closed immediately so nothing it starts can block on
/// input (plan.md §5.3.3), and the process tree is killed on timeout.
/// </summary>
/// <remarks>
/// The npm shim is a <c>.cmd</c>, and per the plan.md §5.1 correction that is launched
/// <b>directly</b> with <c>UseShellExecute = false</c> and <c>ArgumentList</c> — the
/// <c>cmd.exe /c</c> route re-parses the command line and is lossy. None of the arguments used here
/// contain shell hazards anyway, but the direct route keeps this consistent with the runner.
/// </remarks>
public sealed class PreflightProcessRunner : IPreflightProcessRunner
{
    public async Task<PreflightProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                return new PreflightProcessResult(-1, string.Empty, string.Empty, Started: false);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new PreflightProcessResult(-1, string.Empty, ex.Message, Started: false);
        }

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
            return new PreflightProcessResult(-1, string.Empty, string.Empty, TimedOut: true);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            throw;
        }

        return new PreflightProcessResult(
            process.ExitCode,
            await stdout.ConfigureAwait(false),
            await stderr.ConfigureAwait(false));
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
            // Already gone. Nothing to clean up.
        }
    }
}

/// <summary>
/// Runs every gate in plan.md §7 and returns a <see cref="PreflightResult"/> the dashboard renders as
/// pills and the scheduler consults before spending a slot.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here throws.</b> Preflight runs on startup, on every settings change and before every
/// scheduled run; an exception escaping it would take the scheduler down with it. Each check catches
/// its own failures and turns them into a red row, so a broken check degrades to "we could not verify
/// this" rather than to a dead app. Only a cancellation requested by the caller propagates.
/// </para>
/// <para>
/// <b>Error versus warning is the whole point of this class.</b> The split is not cosmetic — the
/// scheduler gates on <see cref="PreflightResult.CanRun"/>, which ignores amber entirely:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>Error</b> — an unattended run started now fails, hangs, or silently does nothing: no CLI, a
///     CLI that will not start, a missing project directory or plan file, no usable login, a missing
///     <c>/caveman:caveman</c> command, or a <c>permissions.ask</c> rule.
///   </description></item>
///   <item><description>
///     <b>Warning</b> — the run proceeds, degraded or unverified: an untrusted folder (measured: only
///     visible-terminal mode gates on it, plan.md §5.3.1 correction), auto mode unavailable (it falls
///     back to <c>acceptEdits</c>), a finished plan, and every git row.
///   </description></item>
/// </list>
/// <para>
/// Two rows deserve their reasoning spelled out. <b>Workspace trust is amber, not red</b>: a full
/// headless run in a never-before-opened directory completed normally on Claude Code 2.1.220 and
/// never consulted the trust flags, so failing preflight on it would block runs that demonstrably
/// work. <b>The caveman row is red</b> because of the plan.md §5.2 measurement: an unnamespaced or
/// missing command makes Claude Code answer "Unknown command: /caveman" with
/// <c>is_error: false</c> and exit code 0 — the prompt is swallowed whole and the run is recorded as
/// a success. That is the one failure this app cannot detect after the fact, so it is checked before
/// the fact, and it is checked for the <em>namespaced</em> command, not for a bare "caveman".
/// </para>
/// </remarks>
/// <summary>
/// Runs the plan.md §7 checks. Behind an interface so the scheduler — which gates every cycle on
/// it — can be tested without standing up a process runner, a trust manager and a credential
/// reader just to prove that a blocked preflight stops a run.
/// </summary>
public interface IPreflightChecker
{
    Task<PreflightResult> CheckAsync(PilotSettings settings, CancellationToken cancellationToken = default);
}

public sealed partial class PreflightChecker : IPreflightChecker
{
    /// <summary>`claude --version` is a local command; longer than this means the CLI is wedged.</summary>
    public static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(15);

    /// <summary>`claude plugin list` reads local state but starts a Node process.</summary>
    public static readonly TimeSpan PluginListTimeout = TimeSpan.FromSeconds(30);

    /// <summary>`git status` on a large repository is still fast; this is a hang detector.</summary>
    public static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(20);

    /// <summary>`claude plugin marketplace add` clones a repository, so it gets real time.</summary>
    public static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The git CLI, resolved from PATH by the process runner.</summary>
    public const string GitExecutable = "git";

    /// <summary>Plugin name as Claude Code knows it. The command id is <c>caveman:caveman</c>.</summary>
    public const string CavemanPluginName = "caveman";

    /// <summary>Marketplace argument from plan.md §5.2's install line.</summary>
    public const string CavemanMarketplace = "JuliusBrussee/caveman";

    /// <summary><c>&lt;plugin&gt;@&lt;marketplace&gt;</c>, the form `claude plugin install` wants.</summary>
    public const string CavemanPluginSpec = "caveman@caveman";

    /// <summary>Manifest of installed plugins under the plugins root.</summary>
    public const string InstalledPluginsFileName = "installed_plugins.json";

    /// <summary>Starter content for the "Create a plan file" fix. Deliberately has unticked boxes.</summary>
    public const string StarterPlanTemplate = """
        # Plan

        The authoritative task list for this project. NightShift reads it before every run and
        picks up from the first unchecked item.

        - [ ] Describe the first task here.
        - [ ] Describe the second task here.
        """;

    static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    readonly IClaudeExecutableLocator _locator;
    readonly IPreflightProcessRunner _processRunner;
    readonly AutoModeProbe _autoModeProbe;
    readonly WorkspaceTrustManager _trustManager;
    readonly PermissionsAskScanner _permissionsScanner;
    readonly IClaudeCredentialReader _credentialReader;
    readonly ILogger<PreflightChecker> _logger;
    readonly TimeProvider _timeProvider;

    /// <param name="pluginsDirectory">
    /// Overrides <c>~/.claude/plugins</c>. Injectable so tests read a temp directory rather than the
    /// developer's own plugin install.
    /// </param>
    public PreflightChecker(
        IClaudeExecutableLocator locator,
        IPreflightProcessRunner processRunner,
        AutoModeProbe autoModeProbe,
        WorkspaceTrustManager trustManager,
        PermissionsAskScanner permissionsScanner,
        IClaudeCredentialReader credentialReader,
        ILogger<PreflightChecker> logger,
        TimeProvider? timeProvider = null,
        string? pluginsDirectory = null)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _autoModeProbe = autoModeProbe ?? throw new ArgumentNullException(nameof(autoModeProbe));
        _trustManager = trustManager ?? throw new ArgumentNullException(nameof(trustManager));
        _permissionsScanner = permissionsScanner ?? throw new ArgumentNullException(nameof(permissionsScanner));
        _credentialReader = credentialReader ?? throw new ArgumentNullException(nameof(credentialReader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        PluginsDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(pluginsDirectory) ? DefaultPluginsDirectory : pluginsDirectory);
    }

    /// <summary><c>~/.claude/plugins</c>.</summary>
    public static string DefaultPluginsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude",
        "plugins");

    /// <summary>The plugins root this instance inspects.</summary>
    public string PluginsDirectory { get; }

    /// <summary>Display name for a row, so the app never has to hard-code these strings.</summary>
    /// <summary>
    /// Remote Control only exists in an interactive session. Measured against Claude Code 2.1.220:
    /// <c>--remote-control</c> in headless <c>-p</c> mode is accepted and then silently ignored, so
    /// a user with the box ticked would otherwise believe it was on. Warning, not error — the run
    /// itself is fine, it just will not be remotely controllable.
    /// </summary>
    static PreflightCheck CheckRemoteControl(PilotSettings settings)
    {
        if (settings.LaunchMode == LaunchMode.Headless)
        {
            return Warning(
                PreflightCheckId.RemoteControl,
                "Remote control is on but the launch mode is Headless, where Claude Code ignores it. " +
                "Switch to Visible terminal to use it.");
        }

        var name = settings.RemoteControlName is { Length: > 0 } explicitName
            ? explicitName
            : Execution.RepositoryName.Resolve(settings.ProjectDirectory);

        return name is null
            ? Warning(
                PreflightCheckId.RemoteControl,
                "Remote control is on, but no session name could be derived from the project directory.")
            : Ok(PreflightCheckId.RemoteControl, $"Remote control will be enabled as '{name}'.");
    }

    public static string NameOf(PreflightCheckId id) => id switch
    {
        PreflightCheckId.ClaudeExecutable => "Claude CLI found",
        PreflightCheckId.ClaudeVersion => "Claude CLI runs",
        PreflightCheckId.ProjectDirectory => "Project directory",
        PreflightCheckId.PlanFile => "Plan file",
        PreflightCheckId.PlanItems => "Plan has work left",
        PreflightCheckId.Credentials => "Claude login",
        PreflightCheckId.CavemanPlugin => "caveman plugin",
        PreflightCheckId.WorkspaceTrust => "Folder trusted",
        PreflightCheckId.AutoMode => "Auto permission mode",
        PreflightCheckId.PermissionsAsk => "No ask rules",
        PreflightCheckId.GitRepository => "Git repository",
        PreflightCheckId.GitWorkingTree => "Working tree clean",
        PreflightCheckId.RemoteControl => "Remote control",
        _ => id.ToString(),
    };

    /// <summary>
    /// Runs every check in plan.md §7's table order. Never throws except for a cancellation the
    /// caller asked for.
    /// </summary>
    public async Task<PreflightResult> CheckAsync(
        PilotSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Normalized() guarantees a non-empty PlanFileName, so the plan check never falls back to a
        // hard-coded "plan.md" behind the user's back.
        var normalized = settings.Normalized();
        var projectDirectory = normalized.ProjectDirectory?.Trim() ?? string.Empty;
        var projectExists = projectDirectory.Length > 0 && SafeDirectoryExists(projectDirectory);

        var checks = new List<PreflightCheck>(12);

        var resolution = await SafeAsync(
            PreflightCheckId.ClaudeExecutable,
            () => Task.FromResult(_locator.Locate(normalized)),
            cancellationToken).ConfigureAwait(false);

        checks.Add(resolution.Failure ?? CheckExecutable(resolution.Value!, normalized));

        checks.Add(await RunAsync(
            PreflightCheckId.ClaudeVersion,
            () => CheckVersionAsync(resolution.Value, cancellationToken),
            cancellationToken).ConfigureAwait(false));

        checks.Add(CheckProjectDirectory(projectDirectory, projectExists));

        var plan = await RunPlanChecksAsync(
            projectDirectory, projectExists, normalized.PlanFileName, cancellationToken)
            .ConfigureAwait(false);
        checks.Add(plan.File);
        checks.Add(plan.Items);

        checks.Add(await RunAsync(
            PreflightCheckId.Credentials,
            () => CheckCredentialsAsync(cancellationToken),
            cancellationToken).ConfigureAwait(false));

        checks.Add(await RunAsync(
            PreflightCheckId.CavemanPlugin,
            () => CheckCavemanAsync(resolution.Value, cancellationToken),
            cancellationToken).ConfigureAwait(false));

        checks.Add(await RunAsync(
            PreflightCheckId.WorkspaceTrust,
            () => CheckTrustAsync(projectDirectory, projectExists, cancellationToken),
            cancellationToken).ConfigureAwait(false));

        var decision = await SafeAsync(
            PreflightCheckId.AutoMode,
            () => _autoModeProbe.DecideAsync(normalized, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        checks.Add(decision.Failure ?? CheckAutoMode(decision.Value!));

        checks.Add(await RunAsync(
            PreflightCheckId.PermissionsAsk,
            () => CheckPermissionsAsync(projectExists ? projectDirectory : null, cancellationToken),
            cancellationToken).ConfigureAwait(false));

        var git = await RunGitChecksAsync(projectDirectory, projectExists, cancellationToken)
            .ConfigureAwait(false);
        checks.Add(git.Repository);
        checks.Add(git.WorkingTree);

        if (normalized.EnableRemoteControl)
        {
            checks.Add(CheckRemoteControl(normalized));
        }

        var result = new PreflightResult(
            checks,
            _timeProvider.GetUtcNow(),
            plan.Counts,
            resolution.Value?.ExecutablePath,
            decision.Value);

        _logger.Log(
            result.HasErrors ? LogLevel.Warning : LogLevel.Information,
            "Preflight: {Summary}",
            result.Summary);

        return result;
    }

    // ── Checks ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>plan.md §7 row 1. The probed paths are the useful part of the failure.</summary>
    static PreflightCheck CheckExecutable(ClaudeExecutableResolution resolution, PilotSettings settings)
    {
        if (resolution.IsFound)
        {
            return Ok(
                PreflightCheckId.ClaudeExecutable,
                $"Found at {resolution.ExecutablePath} ({Describe(resolution.Source)}).");
        }

        return new PreflightCheck(
            PreflightCheckId.ClaudeExecutable,
            NameOf(PreflightCheckId.ClaudeExecutable),
            PreflightStatus.Error,
            "Claude Code was not found on PATH or in any known install location.")
        {
            Fix = new PreflightFixAction(
                PreflightFixCommand.SetClaudeExecutablePath,
                "Set the Claude Code path…",
                string.IsNullOrWhiteSpace(settings.ClaudeExecutablePath)
                    ? null
                    : settings.ClaudeExecutablePath),

            // Without these, "not found on PATH" gives the user nothing to act on — the real cause is
            // almost always that their npm prefix is not on this process's PATH.
            Details = resolution.ProbedPaths,
        };
    }

    /// <summary>plan.md §7 row 2. No fix action: the table has none, and there is nothing to offer.</summary>
    async Task<PreflightCheck> CheckVersionAsync(
        ClaudeExecutableResolution? resolution,
        CancellationToken cancellationToken)
    {
        if (resolution is not { IsFound: true })
        {
            return Error(
                PreflightCheckId.ClaudeVersion,
                "Not checked: Claude Code was not found, so there is nothing to run.");
        }

        var output = await _processRunner
            .RunAsync(resolution.ExecutablePath, ["--version"], null, VersionTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (!output.Started)
        {
            return Error(
                PreflightCheckId.ClaudeVersion,
                $"Claude Code failed to start: {FirstLine(output.StandardError)}".TrimEnd(':', ' '));
        }

        if (output.TimedOut)
        {
            return Error(
                PreflightCheckId.ClaudeVersion,
                $"`claude --version` did not finish within {VersionTimeout.TotalSeconds:0} seconds.");
        }

        if (output.ExitCode != 0)
        {
            var detail = FirstLine(output.StandardError);
            return Error(
                PreflightCheckId.ClaudeVersion,
                detail.Length == 0
                    ? $"`claude --version` exited with code {output.ExitCode}."
                    : $"`claude --version` exited with code {output.ExitCode}: {detail}");
        }

        var match = VersionPattern().Match(output.StandardOutput);
        return Ok(
            PreflightCheckId.ClaudeVersion,
            match.Success
                ? $"Claude Code {match.Value} responded."
                : $"`claude --version` ran, but printed no recognisable version: {FirstLine(output.StandardOutput)}");
    }

    /// <summary>plan.md §7 row 3.</summary>
    static PreflightCheck CheckProjectDirectory(string projectDirectory, bool exists)
    {
        if (projectDirectory.Length == 0)
        {
            return new PreflightCheck(
                PreflightCheckId.ProjectDirectory,
                NameOf(PreflightCheckId.ProjectDirectory),
                PreflightStatus.Error,
                "No project directory has been chosen yet.")
            {
                Fix = new PreflightFixAction(PreflightFixCommand.SetProjectDirectory, "Pick a directory…"),
            };
        }

        if (!exists)
        {
            return new PreflightCheck(
                PreflightCheckId.ProjectDirectory,
                NameOf(PreflightCheckId.ProjectDirectory),
                PreflightStatus.Error,
                $"Directory not found: {projectDirectory}")
            {
                Fix = new PreflightFixAction(
                    PreflightFixCommand.SetProjectDirectory, "Pick a directory…", projectDirectory),
            };
        }

        return Ok(PreflightCheckId.ProjectDirectory, $"{projectDirectory} exists.");
    }

    /// <summary>
    /// plan.md §7 row 4 plus the "is there anything left to do" row. Both come out of one read, so a
    /// plan file cannot be seen to exist and then vanish between the two.
    /// </summary>
    async Task<(PreflightCheck File, PreflightCheck Items, PlanItemCounts? Counts)> RunPlanChecksAsync(
        string projectDirectory,
        bool projectExists,
        string planFileName,
        CancellationToken cancellationToken)
    {
        if (!projectExists)
        {
            return (
                Error(PreflightCheckId.PlanFile, "Not checked: the project directory does not exist."),
                Warning(PreflightCheckId.PlanItems, "Not checked: there is no plan file to read."),
                null);
        }

        string planPath;
        try
        {
            planPath = Path.Combine(projectDirectory, planFileName);
        }
        catch (ArgumentException ex)
        {
            return (
                Error(PreflightCheckId.PlanFile, $"`{planFileName}` is not a usable file name: {ex.Message}"),
                Warning(PreflightCheckId.PlanItems, "Not checked: there is no plan file to read."),
                null);
        }

        if (!File.Exists(planPath))
        {
            var missing = new PreflightCheck(
                PreflightCheckId.PlanFile,
                NameOf(PreflightCheckId.PlanFile),
                PreflightStatus.Error,
                $"No {planFileName} in {projectDirectory}.")
            {
                Fix = new PreflightFixAction(
                    PreflightFixCommand.CreatePlanFile, $"Create a starter {planFileName}", planPath),
            };

            return (
                missing,
                Warning(PreflightCheckId.PlanItems, "Not checked: there is no plan file to read."),
                null);
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(planPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (
                Error(PreflightCheckId.PlanFile, $"{planPath} exists but could not be read: {ex.Message}"),
                Error(PreflightCheckId.PlanItems, $"Not checked: {planFileName} could not be read."),
                null);
        }

        var counts = CountPlanItems(text);
        var file = Ok(PreflightCheckId.PlanFile, $"{planPath} exists ({counts.Summary}).");

        return (file, PlanItemsCheck(counts, planPath, planFileName), counts);
    }

    /// <summary>
    /// The extra row: an unattended run against a plan with nothing left to tick just burns a slot
    /// and a chunk of the weekly quota to be told there is nothing to do.
    /// </summary>
    static PreflightCheck PlanItemsCheck(PlanItemCounts counts, string planPath, string planFileName)
    {
        if (counts.Total == 0)
        {
            return new PreflightCheck(
                PreflightCheckId.PlanItems,
                NameOf(PreflightCheckId.PlanItems),
                PreflightStatus.Warning,
                $"{planFileName} has no `- [ ]` checkboxes, so a run has nothing to pick up.")
            {
                Fix = new PreflightFixAction(PreflightFixCommand.OpenPlanFile, "Open the plan file", planPath),
            };
        }

        if (!counts.HasRemainingWork)
        {
            var blocked = counts.Blocked == 1
                ? " 1 item is marked blocked (`- [!]`), which a run will skip."
                : counts.Blocked > 1
                    ? $" {counts.Blocked} items are marked blocked (`- [!]`), which a run will skip."
                    : string.Empty;

            return new PreflightCheck(
                PreflightCheckId.PlanItems,
                NameOf(PreflightCheckId.PlanItems),
                PreflightStatus.Warning,
                $"Every checkbox in {planFileName} is already ticked ({counts.Summary}).{blocked}")
            {
                Fix = new PreflightFixAction(PreflightFixCommand.OpenPlanFile, "Open the plan file", planPath),
            };
        }

        return Ok(
            PreflightCheckId.PlanItems,
            $"{counts.Remaining} item{(counts.Remaining == 1 ? "" : "s")} left to do ({counts.Summary}).");
    }

    /// <summary>plan.md §7 row 5. The token is never read out of the credential, let alone reported.</summary>
    async Task<PreflightCheck> CheckCredentialsAsync(CancellationToken cancellationToken)
    {
        var credentials = await _credentialReader.ReadAsync(cancellationToken).ConfigureAwait(false);

        if (credentials is null)
        {
            return new PreflightCheck(
                PreflightCheckId.Credentials,
                NameOf(PreflightCheckId.Credentials),
                PreflightStatus.Error,
                "No Claude login was found on this machine.")
            {
                Fix = new PreflightFixAction(
                    PreflightFixCommand.ShowLoginInstructions, "How to log in…"),
            };
        }

        var now = _timeProvider.GetUtcNow();
        if (credentials.IsExpiredAt(now))
        {
            return new PreflightCheck(
                PreflightCheckId.Credentials,
                NameOf(PreflightCheckId.Credentials),
                PreflightStatus.Error,
                $"The Claude login expired at {credentials.ExpiresAt:u}.")
            {
                Fix = new PreflightFixAction(
                    PreflightFixCommand.ShowLoginInstructions, "How to log in…"),
            };
        }

        return Ok(
            PreflightCheckId.Credentials,
            credentials.ExpiresAt is { } expiry
                ? $"Logged in; the credential is valid until {expiry:u}."
                : "Logged in; the credential store did not state an expiry.");
    }

    /// <summary>
    /// plan.md §7 row 6, rewritten around the §5.2 correction. What has to be true is that the
    /// <b>namespaced</b> command <c>caveman:caveman</c> resolves — a bare <c>/caveman</c> is answered
    /// with "Unknown command" and the entire prompt is discarded on a run the CLI still calls a
    /// success.
    /// </summary>
    /// <remarks>
    /// Two independent sources, because neither is sufficient. The plan prescribes
    /// <c>claude plugin list</c>, but its output format is not something this app can pin down, and
    /// it only tells us about the <em>plugin</em>. The filesystem tells us about the
    /// <em>command</em>, which is what the prompt actually references — so the filesystem wins when
    /// it has an answer, and the CLI is the fallback for a future layout this code has not seen.
    /// </remarks>
    async Task<PreflightCheck> CheckCavemanAsync(
        ClaudeExecutableResolution? resolution,
        CancellationToken cancellationToken)
    {
        var probed = new List<string>();
        var filesystem = InspectPluginsDirectory(probed);

        if (filesystem == CavemanEvidence.Installed)
        {
            return Ok(
                PreflightCheckId.CavemanPlugin,
                $"The caveman plugin is installed and provides `{PromptBuilder.CavemanCommand}`.")
                with
            { Details = probed };
        }

        if (filesystem == CavemanEvidence.PluginWithoutCommand)
        {
            return InstallFix(
                $"The caveman plugin is installed but does not provide a `{CavemanPluginName}` command, " +
                $"so `{PromptBuilder.CavemanCommand}` will not resolve and the whole prompt would be discarded.",
                probed);
        }

        var cli = await AskCliAboutCavemanAsync(resolution, probed, cancellationToken).ConfigureAwait(false);

        if (cli == CavemanEvidence.Installed)
        {
            return Ok(
                PreflightCheckId.CavemanPlugin,
                "`claude plugin list` reports the caveman plugin as installed.") with { Details = probed };
        }

        if (filesystem == CavemanEvidence.Absent || cli == CavemanEvidence.Absent)
        {
            return InstallFix(
                $"The caveman plugin is not installed, so `{PromptBuilder.CavemanCommand}` would be an " +
                "unknown command and the run would silently do nothing.",
                probed);
        }

        return Warning(
            PreflightCheckId.CavemanPlugin,
            "Could not verify that the caveman plugin is installed.") with { Details = probed };

        static PreflightCheck InstallFix(string message, IReadOnlyList<string> details) =>
            new PreflightCheck(
                PreflightCheckId.CavemanPlugin,
                NameOf(PreflightCheckId.CavemanPlugin),
                PreflightStatus.Error,
                message)
            {
                Fix = new PreflightFixAction(
                    PreflightFixCommand.InstallCavemanPlugin, "Install the caveman plugin"),
                Details = details,
            };
    }

    /// <summary>plan.md §7 row 7 — amber, per the §5.3.1 correction.</summary>
    async Task<PreflightCheck> CheckTrustAsync(
        string projectDirectory,
        bool projectExists,
        CancellationToken cancellationToken)
    {
        if (!projectExists)
        {
            return Warning(
                PreflightCheckId.WorkspaceTrust,
                "Not checked: the project directory does not exist.");
        }

        var status = await _trustManager.CheckAsync(projectDirectory, cancellationToken).ConfigureAwait(false);

        if (status.IsTrusted)
        {
            return Ok(PreflightCheckId.WorkspaceTrust, $"{projectDirectory} is trusted.")
                with
            { Details = status.Keys };
        }

        // Amber, not red. Measured on Claude Code 2.1.220: a headless run in a never-before-opened
        // directory completed normally and never consulted these flags, so a red row here would
        // block runs that demonstrably work. It stays visible because visible-terminal mode (§5.5)
        // does still show the dialog.
        return new PreflightCheck(
            PreflightCheckId.WorkspaceTrust,
            NameOf(PreflightCheckId.WorkspaceTrust),
            PreflightStatus.Warning,
            status.Problem is { } problem
                ? $"Could not confirm the folder is trusted ({problem}). Headless runs proceed anyway; " +
                  "a visible terminal would stop and ask."
                : "Folder not yet trusted. Headless runs proceed anyway; a visible terminal would stop and ask.")
        {
            Fix = new PreflightFixAction(
                PreflightFixCommand.TrustProjectFolder, "Trust this folder", projectDirectory),
            Details = status.MissingKeys,
        };
    }

    /// <summary>plan.md §7 row 8 — warning only; it exists to explain the downgrade.</summary>
    static PreflightCheck CheckAutoMode(PermissionModeDecision decision)
    {
        if (!decision.IsFallback)
        {
            return Ok(
                PreflightCheckId.AutoMode,
                decision.Configured == PermissionMode.Auto
                    ? "Auto mode is available; runs use `--permission-mode auto`."
                    : $"Runs use the configured `--permission-mode {decision.Configured.ToCliValue()}`; " +
                      "auto mode is not required.");
        }

        return new PreflightCheck(
            PreflightCheckId.AutoMode,
            NameOf(PreflightCheckId.AutoMode),
            PreflightStatus.Warning,
            decision.Explanation ??
            $"Runs will use `{decision.Effective.ToCliValue()}` rather than the configured " +
            $"`{decision.Configured.ToCliValue()}`.")
        {
            Fix = decision.Effective == PermissionMode.AcceptEdits
                ? new PreflightFixAction(PreflightFixCommand.ReprobeAutoMode, "Check auto mode again")
                : null,
            Details = [$"--permission-mode {decision.Effective.ToCliValue()}", $"--allowedTools {decision.AllowedTools}"],
        };
    }

    /// <summary>
    /// plan.md §7 row 9 — red, and one of the few rows that genuinely is. An ask rule is evaluated
    /// before the auto-mode classifier and always forces a prompt, so an unattended run sits there
    /// until the stall detector kills it (plan.md §5.3.2).
    /// </summary>
    async Task<PreflightCheck> CheckPermissionsAsync(
        string? projectDirectory,
        CancellationToken cancellationToken)
    {
        var scan = await _permissionsScanner.ScanAsync(projectDirectory, cancellationToken).ConfigureAwait(false);

        if (scan.HasBlockers)
        {
            var blockers = scan.Findings
                .Where(finding => finding.Kind != PermissionsFindingKind.Unreadable)
                .ToArray();

            var rules = blockers
                .Where(finding => finding.Kind == PermissionsFindingKind.AskRule)
                .Select(finding => finding.Rule)
                .ToArray();

            var message = rules.Length > 0
                ? $"Ask rules will pause an unattended run: {string.Join(", ", rules.Select(rule => $"`{rule}`"))}"
                : "MCP servers that require user interaction will pause an unattended run: " +
                  string.Join(", ", blockers.Select(finding => $"`{finding.Rule}`"));

            return new PreflightCheck(
                PreflightCheckId.PermissionsAsk,
                NameOf(PreflightCheckId.PermissionsAsk),
                PreflightStatus.Error,
                message)
            {
                Fix = new PreflightFixAction(
                    PreflightFixCommand.OpenSettingsFile, "Open the settings file", blockers[0].FilePath),
                Details = blockers.Select(finding => finding.ToString()).ToArray(),
            };
        }

        if (scan.HasUnreadableFiles)
        {
            var unreadable = scan.Findings
                .Where(finding => finding.Kind == PermissionsFindingKind.Unreadable)
                .ToArray();

            return new PreflightCheck(
                PreflightCheckId.PermissionsAsk,
                NameOf(PreflightCheckId.PermissionsAsk),
                PreflightStatus.Warning,
                $"Could not check {unreadable.Length} settings file" +
                $"{(unreadable.Length == 1 ? "" : "s")} for ask rules.")
            {
                Fix = new PreflightFixAction(
                    PreflightFixCommand.OpenSettingsFile, "Open the settings file", unreadable[0].FilePath),
                Details = unreadable.Select(finding => finding.ToString()).ToArray(),
            };
        }

        return Ok(
            PreflightCheckId.PermissionsAsk,
            $"No `permissions.ask` rules in the {scan.Files.Count} settings file" +
            $"{(scan.Files.Count == 1 ? "" : "s")} checked.")
            with
        { Details = scan.Files.Select(file => file.Path).ToArray() };
    }

    /// <summary>
    /// plan.md §7 rows 10 and 11. Both are warning-only, including "git is not installed" — a machine
    /// without git is a worse place to run this app, but it is not a reason to refuse to run.
    /// </summary>
    async Task<(PreflightCheck Repository, PreflightCheck WorkingTree)> RunGitChecksAsync(
        string projectDirectory,
        bool projectExists,
        CancellationToken cancellationToken)
    {
        if (!projectExists)
        {
            var reason = "Not checked: the project directory does not exist.";
            return (
                Warning(PreflightCheckId.GitRepository, reason),
                Warning(PreflightCheckId.GitWorkingTree, reason));
        }

        PreflightProcessResult inside;
        try
        {
            inside = await _processRunner.RunAsync(
                GitExecutable,
                ["rev-parse", "--is-inside-work-tree"],
                projectDirectory,
                GitTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var reason = $"Could not run git: {FirstLine(ex.Message)}";
            _logger.LogWarning(ex, "The git preflight checks could not run.");
            return (
                Warning(PreflightCheckId.GitRepository, reason),
                Warning(PreflightCheckId.GitWorkingTree, reason));
        }

        if (!inside.Started)
        {
            const string reason =
                "git is not installed, or is not on PATH, so this could not be checked.";
            return (
                Warning(PreflightCheckId.GitRepository, reason),
                Warning(PreflightCheckId.GitWorkingTree, reason));
        }

        if (inside.TimedOut)
        {
            var reason = $"`git rev-parse` did not finish within {GitTimeout.TotalSeconds:0} seconds.";
            return (
                Warning(PreflightCheckId.GitRepository, reason),
                Warning(PreflightCheckId.GitWorkingTree, reason));
        }

        var isRepository = inside.ExitCode == 0 &&
            inside.StandardOutput.Contains("true", StringComparison.OrdinalIgnoreCase);

        if (!isRepository)
        {
            var repository = new PreflightCheck(
                PreflightCheckId.GitRepository,
                NameOf(PreflightCheckId.GitRepository),
                PreflightStatus.Warning,
                $"{projectDirectory} is not a git repository — a bad run will not be recoverable.")
            {
                Fix = new PreflightFixAction(
                    PreflightFixCommand.InitializeGitRepository, "Run `git init`", projectDirectory),
            };

            return (
                repository,
                Warning(
                    PreflightCheckId.GitWorkingTree,
                    "Not checked: the directory is not a git repository."));
        }

        return (
            Ok(PreflightCheckId.GitRepository, $"{projectDirectory} is a git repository."),
            await CheckWorkingTreeAsync(projectDirectory, cancellationToken).ConfigureAwait(false));
    }

    async Task<PreflightCheck> CheckWorkingTreeAsync(
        string projectDirectory,
        CancellationToken cancellationToken)
    {
        var status = await _processRunner.RunAsync(
            GitExecutable,
            ["status", "--porcelain"],
            projectDirectory,
            GitTimeout,
            cancellationToken).ConfigureAwait(false);

        if (!status.Succeeded)
        {
            return Warning(
                PreflightCheckId.GitWorkingTree,
                status.TimedOut
                    ? $"`git status` did not finish within {GitTimeout.TotalSeconds:0} seconds."
                    : $"`git status` failed: {FirstLine(status.StandardError)}".TrimEnd(':', ' '));
        }

        var changes = status.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        if (changes.Length == 0)
        {
            return Ok(PreflightCheckId.GitWorkingTree, "The working tree is clean.");
        }

        return Warning(
            PreflightCheckId.GitWorkingTree,
            $"{changes.Length} uncommitted change{(changes.Length == 1 ? "" : "s")} present; " +
            "a run's own commits will be mixed in with them.")
            with
        { Details = changes };
    }

    // ── Fixes ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Carries out the fixes Core owns. The app calls this for a
    /// <see cref="PreflightFixAction"/> whose <see cref="PreflightFixAction.IsAppliedByCore"/> is
    /// true, and handles the rest itself — pickers, dialogs and editors are not Core's business.
    /// Never throws; a failure comes back as <see cref="PreflightFixOutcome.Failed"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="PreflightFixCommand.TrustProjectFolder"/> writes to <c>~/.claude.json</c>, which the
    /// CLI rewrites continuously while a session is live (plan.md §5.3.1). The caller must hold the
    /// run gate, exactly as it must for <see cref="WorkspaceTrustManager.ApplyAsync"/> itself.
    /// </remarks>
    public async Task<PreflightFixResult> ApplyFixAsync(
        PreflightFixAction fix,
        PilotSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fix);
        ArgumentNullException.ThrowIfNull(settings);

        if (!fix.IsAppliedByCore)
        {
            return new PreflightFixResult(
                fix.Command,
                PreflightFixOutcome.HandledByTheApp,
                $"`{fix.Command}` is presented by the app; NightShift.Core does not perform it.");
        }

        try
        {
            return fix.Command switch
            {
                PreflightFixCommand.CreatePlanFile =>
                    await CreatePlanFileAsync(fix, settings, cancellationToken).ConfigureAwait(false),
                PreflightFixCommand.TrustProjectFolder =>
                    await TrustAsync(fix, settings, cancellationToken).ConfigureAwait(false),
                PreflightFixCommand.InstallCavemanPlugin =>
                    await InstallCavemanAsync(settings, cancellationToken).ConfigureAwait(false),
                PreflightFixCommand.ReprobeAutoMode =>
                    await ReprobeAsync(cancellationToken).ConfigureAwait(false),
                PreflightFixCommand.InitializeGitRepository =>
                    await GitInitAsync(fix, settings, cancellationToken).ConfigureAwait(false),
                _ => new PreflightFixResult(
                    fix.Command, PreflightFixOutcome.Failed, $"`{fix.Command}` is not implemented."),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The preflight fix {Command} failed.", fix.Command);
            return new PreflightFixResult(fix.Command, PreflightFixOutcome.Failed, ex.Message);
        }
    }

    async Task<PreflightFixResult> CreatePlanFileAsync(
        PreflightFixAction fix,
        PilotSettings settings,
        CancellationToken cancellationToken)
    {
        var path = fix.Target ?? Path.Combine(
            settings.ProjectDirectory, settings.Normalized().PlanFileName);

        if (File.Exists(path))
        {
            return new PreflightFixResult(
                fix.Command, PreflightFixOutcome.AlreadyDone, $"{path} already exists.");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, StarterPlanTemplate, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Created a starter plan file at {Path}.", path);
        return new PreflightFixResult(fix.Command, PreflightFixOutcome.Applied, $"Created {path}.");
    }

    async Task<PreflightFixResult> TrustAsync(
        PreflightFixAction fix,
        PilotSettings settings,
        CancellationToken cancellationToken)
    {
        var directory = fix.Target ?? settings.ProjectDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            return new PreflightFixResult(
                fix.Command, PreflightFixOutcome.Failed, "There is no project directory to trust.");
        }

        var applied = await _trustManager.ApplyAsync(directory, cancellationToken).ConfigureAwait(false);

        return applied.Outcome switch
        {
            WorkspaceTrustOutcome.AlreadyTrusted => new PreflightFixResult(
                fix.Command, PreflightFixOutcome.AlreadyDone, $"{directory} was already trusted."),
            WorkspaceTrustOutcome.Applied => new PreflightFixResult(
                fix.Command,
                PreflightFixOutcome.Applied,
                $"Trusted {string.Join(" and ", applied.Keys)} in {_trustManager.ConfigPath}."),
            _ => new PreflightFixResult(
                fix.Command,
                PreflightFixOutcome.Failed,
                applied.Error ?? $"Could not write the trust keys to {_trustManager.ConfigPath}."),
        };
    }

    /// <summary>Runs the two commands from plan.md §5.2, stopping at the first failure.</summary>
    async Task<PreflightFixResult> InstallCavemanAsync(
        PilotSettings settings,
        CancellationToken cancellationToken)
    {
        var resolution = _locator.Locate(settings);
        if (!resolution.IsFound)
        {
            return new PreflightFixResult(
                PreflightFixCommand.InstallCavemanPlugin,
                PreflightFixOutcome.Failed,
                "Claude Code was not found, so the plugin cannot be installed from here.");
        }

        string[][] commands =
        [
            ["plugin", "marketplace", "add", CavemanMarketplace],
            ["plugin", "install", CavemanPluginSpec],
        ];

        foreach (var arguments in commands)
        {
            var output = await _processRunner
                .RunAsync(resolution.ExecutablePath, arguments, null, InstallTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (!output.Succeeded)
            {
                var detail = output.Started
                    ? output.TimedOut
                        ? "it did not finish in time."
                        : $"exit code {output.ExitCode}: {FirstLine(output.StandardError)}"
                    : "the process could not be started.";

                return new PreflightFixResult(
                    PreflightFixCommand.InstallCavemanPlugin,
                    PreflightFixOutcome.Failed,
                    $"`claude {string.Join(' ', arguments)}` failed — {detail}");
            }
        }

        _logger.LogInformation("Installed the caveman plugin from {Marketplace}.", CavemanMarketplace);
        return new PreflightFixResult(
            PreflightFixCommand.InstallCavemanPlugin,
            PreflightFixOutcome.Applied,
            $"Installed {CavemanPluginSpec}. Re-run preflight to confirm.");
    }

    async Task<PreflightFixResult> ReprobeAsync(CancellationToken cancellationToken)
    {
        var status = await _autoModeProbe.ReprobeAsync(cancellationToken).ConfigureAwait(false);

        return status.IsAvailable
            ? new PreflightFixResult(
                PreflightFixCommand.ReprobeAutoMode, PreflightFixOutcome.Applied, "Auto mode is now available.")
            : new PreflightFixResult(
                PreflightFixCommand.ReprobeAutoMode,
                PreflightFixOutcome.Failed,
                status.Detail ?? "Auto mode is still unavailable.");
    }

    async Task<PreflightFixResult> GitInitAsync(
        PreflightFixAction fix,
        PilotSettings settings,
        CancellationToken cancellationToken)
    {
        var directory = fix.Target ?? settings.ProjectDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !SafeDirectoryExists(directory))
        {
            return new PreflightFixResult(
                fix.Command, PreflightFixOutcome.Failed, "The project directory does not exist.");
        }

        var output = await _processRunner
            .RunAsync(GitExecutable, ["init"], directory, GitTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (!output.Started)
        {
            return new PreflightFixResult(
                fix.Command, PreflightFixOutcome.Failed, "git is not installed, or is not on PATH.");
        }

        return output.Succeeded
            ? new PreflightFixResult(
                fix.Command, PreflightFixOutcome.Applied, $"Initialised a git repository in {directory}.")
            : new PreflightFixResult(
                fix.Command,
                PreflightFixOutcome.Failed,
                $"`git init` failed: {FirstLine(output.StandardError)}".TrimEnd(':', ' '));
    }

    // ── Plan parsing ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Counts <c>- [ ]</c>, <c>- [x]</c> and <c>- [!]</c> the way plan.md §9.1 describes.
    /// </summary>
    /// <remarks>
    /// Fenced code blocks are skipped. A plan that documents its own conventions — as this repo's
    /// does — shows example checkboxes inside a fence, and counting those would make the dashboard's
    /// "12 of 30" quietly wrong. <c>*</c> and <c>+</c> bullets are accepted alongside <c>-</c>
    /// because CommonMark treats them identically and a user's editor may rewrite them.
    /// </remarks>
    public static PlanItemCounts CountPlanItems(string? planText)
    {
        if (string.IsNullOrEmpty(planText))
        {
            return PlanItemCounts.Empty;
        }

        var completed = 0;
        var remaining = 0;
        var blocked = 0;
        var inFence = false;

        foreach (var line in planText.Split('\n'))
        {
            var trimmed = line.TrimStart().TrimEnd('\r');

            if (trimmed.StartsWith("```", StringComparison.Ordinal) ||
                trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                continue;
            }

            var match = CheckboxPattern().Match(line);
            if (!match.Success)
            {
                continue;
            }

            switch (match.Groups["mark"].ValueSpan[0])
            {
                case 'x':
                case 'X':
                    completed++;
                    break;
                case '!':
                    blocked++;
                    break;
                default:
                    remaining++;
                    break;
            }
        }

        return new PlanItemCounts(completed, remaining, blocked);
    }

    /// <summary>A markdown task-list item at the start of a line, with any of the three marks.</summary>
    [GeneratedRegex(@"^[ \t]*[-*+][ \t]+\[(?<mark>[ xX!])\](?=[ \t]|\r?$)", RegexOptions.CultureInvariant)]
    private static partial Regex CheckboxPattern();

    /// <summary>`claude --version` prints e.g. `2.1.220 (Claude Code)`.</summary>
    [GeneratedRegex(@"\d+\.\d+(\.\d+)*(-[0-9A-Za-z.\-]+)?", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    // ── caveman detection ──────────────────────────────────────────────────────────────────────

    /// <summary>How sure we are that <c>caveman:caveman</c> will resolve.</summary>
    enum CavemanEvidence
    {
        /// <summary>This source could not answer.</summary>
        Unknown,

        /// <summary>This source is confident the plugin is not there.</summary>
        Absent,

        /// <summary>The plugin is installed but has no <c>caveman</c> command — the §5.2 trap.</summary>
        PluginWithoutCommand,

        /// <summary>The command will resolve.</summary>
        Installed,
    }

    /// <summary>
    /// Reads <c>~/.claude/plugins/installed_plugins.json</c> and confirms the installed plugin really
    /// ships a <c>caveman</c> command.
    /// </summary>
    /// <remarks>
    /// <b>plan.md §7 is out of date here.</b> It says to "check for a <c>caveman</c> directory under
    /// <c>~/.claude/plugins/</c>". Measured 2026-07-26 on a machine with the plugin installed, no such
    /// directory exists: the root holds <c>installed_plugins.json</c> (keyed
    /// <c>"caveman@caveman"</c>, with an <c>installPath</c> into <c>plugins/cache/…</c>),
    /// <c>known_marketplaces.json</c>, and <c>cache/</c> plus <c>marketplaces/</c> subdirectories. The
    /// manifest is therefore the primary source and the plan's directory probe is kept only as a
    /// fallback for an older layout.
    /// </remarks>
    CavemanEvidence InspectPluginsDirectory(List<string> probed)
    {
        var manifest = Path.Combine(PluginsDirectory, InstalledPluginsFileName);
        probed.Add(manifest);

        if (!SafeDirectoryExists(PluginsDirectory))
        {
            return CavemanEvidence.Unknown;
        }

        if (!File.Exists(manifest))
        {
            return LegacyDirectoryEvidence(probed);
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(
                File.ReadAllText(manifest), nodeOptions: null, DocumentOptions) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Could not read {Path}; falling back to `claude plugin list`.", manifest);
            return CavemanEvidence.Unknown;
        }

        if (root?["plugins"] is not JsonObject plugins)
        {
            return CavemanEvidence.Unknown;
        }

        var found = false;

        foreach (var (key, value) in plugins)
        {
            // Keys are "<plugin>@<marketplace>", e.g. "caveman@caveman".
            var name = key.Split('@', 2)[0];
            if (!string.Equals(name, CavemanPluginName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            found = true;

            foreach (var installPath in InstallPaths(value))
            {
                probed.Add(installPath);

                if (ProvidesCavemanCommand(installPath))
                {
                    return CavemanEvidence.Installed;
                }
            }
        }

        return found ? CavemanEvidence.PluginWithoutCommand : CavemanEvidence.Absent;
    }

    /// <summary>Every <c>installPath</c> under one manifest entry (the value is an array of installs).</summary>
    static IEnumerable<string> InstallPaths(JsonNode? entry)
    {
        var installs = entry switch
        {
            JsonArray array => array.AsEnumerable(),
            JsonObject single => [single],
            _ => [],
        };

        foreach (var install in installs)
        {
            if (install is JsonObject obj &&
                obj["installPath"] is JsonValue value &&
                value.TryGetValue<string>(out var path) &&
                !string.IsNullOrWhiteSpace(path))
            {
                yield return path;
            }
        }
    }

    /// <summary>
    /// True when <paramref name="installPath"/> contains <c>commands/caveman.md</c> (or
    /// <c>.toml</c>) — the file that makes <c>/caveman:caveman</c> a real command rather than the
    /// "Unknown command" that swallows the prompt.
    /// </summary>
    static bool ProvidesCavemanCommand(string installPath)
    {
        try
        {
            var commands = Path.Combine(installPath, "commands");
            return File.Exists(Path.Combine(commands, CavemanPluginName + ".md")) ||
                   File.Exists(Path.Combine(commands, CavemanPluginName + ".toml"));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The plan.md §7 probe, kept as a fallback for a layout without the manifest.</summary>
    CavemanEvidence LegacyDirectoryEvidence(List<string> probed)
    {
        var legacy = Path.Combine(PluginsDirectory, CavemanPluginName);
        probed.Add(legacy);

        return SafeDirectoryExists(legacy) ? CavemanEvidence.Installed : CavemanEvidence.Absent;
    }

    /// <summary>plan.md §7's prescribed probe, used when the filesystem could not answer.</summary>
    async Task<CavemanEvidence> AskCliAboutCavemanAsync(
        ClaudeExecutableResolution? resolution,
        List<string> probed,
        CancellationToken cancellationToken)
    {
        if (resolution is not { IsFound: true })
        {
            return CavemanEvidence.Unknown;
        }

        probed.Add($"{resolution.ExecutablePath} plugin list");

        var output = await _processRunner
            .RunAsync(resolution.ExecutablePath, ["plugin", "list"], null, PluginListTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (!output.Succeeded)
        {
            return CavemanEvidence.Unknown;
        }

        return output.StandardOutput.Contains(CavemanPluginName, StringComparison.OrdinalIgnoreCase)
            ? CavemanEvidence.Installed
            : CavemanEvidence.Absent;
    }

    // ── Plumbing ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Runs one check, turning any failure into a red row rather than an exception.</summary>
    async Task<PreflightCheck> RunAsync(
        PreflightCheckId id,
        Func<Task<PreflightCheck>> check,
        CancellationToken cancellationToken)
    {
        try
        {
            return await check().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The preflight check {Check} threw.", id);
            return Error(id, $"This check could not be completed: {FirstLine(ex.Message)}");
        }
    }

    /// <summary>
    /// Same, for the two checks whose value is also carried on <see cref="PreflightResult"/>. On
    /// failure the caller uses <c>Failure</c> as the row and leaves the value null.
    /// </summary>
    async Task<(T? Value, PreflightCheck? Failure)> SafeAsync<T>(
        PreflightCheckId id,
        Func<Task<T>> produce,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return (await produce().ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The preflight check {Check} threw.", id);
            return (null, Error(id, $"This check could not be completed: {FirstLine(ex.Message)}"));
        }
    }

    static PreflightCheck Ok(PreflightCheckId id, string message) =>
        new(id, NameOf(id), PreflightStatus.Ok, message);

    static PreflightCheck Warning(PreflightCheckId id, string message) =>
        new(id, NameOf(id), PreflightStatus.Warning, message);

    static PreflightCheck Error(PreflightCheckId id, string message) =>
        new(id, NameOf(id), PreflightStatus.Error, message);

    static string Describe(ClaudeExecutableSource source) => source switch
    {
        ClaudeExecutableSource.ConfiguredPath => "the configured path",
        ClaudeExecutableSource.SearchPath => "PATH",
        ClaudeExecutableSource.WellKnownLocation => "a known install location",
        _ => "an unknown source",
    };

    /// <summary>A settings file can hold arbitrary text; a path probe must never throw out of a check.</summary>
    static bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var trimmed = text.Trim();
        var newline = trimmed.IndexOf('\n');
        var line = newline < 0 ? trimmed : trimmed[..newline].TrimEnd();

        return line.Length <= 200 ? line : line[..200] + "…";
    }
}
