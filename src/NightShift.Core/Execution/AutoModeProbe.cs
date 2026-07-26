using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using NightShift.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace NightShift.Core.Execution;

/// <summary>Raw outcome of one <c>claude auto-mode config</c> invocation.</summary>
public sealed record AutoModeProbeOutput(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut = false,
    bool Started = true);

/// <summary>
/// Spawns the probe process. Exists so <see cref="AutoModeProbe"/> is unit testable without a real
/// <c>claude</c> on the machine — no test in this repo may ever launch the CLI.
/// </summary>
public interface IAutoModeProbeRunner
{
    Task<AutoModeProbeOutput> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>Whether <c>--permission-mode auto</c> can be used on this account and model.</summary>
public enum AutoModeAvailability
{
    /// <summary>Not probed yet.</summary>
    Unknown,

    /// <summary>`claude auto-mode config` exited 0 with parseable JSON.</summary>
    Available,

    /// <summary>Anything else: unknown subcommand, non-zero exit, garbage output, no CLI, timeout.</summary>
    Unavailable,
}

/// <param name="Detail">Human-readable reason, suitable for the amber preflight pill.</param>
/// <param name="ProbedAt">Null until the first probe completes.</param>
public sealed record AutoModeStatus(
    AutoModeAvailability Availability,
    string? Detail,
    DateTimeOffset? ProbedAt)
{
    public static AutoModeStatus NotProbed { get; } = new(AutoModeAvailability.Unknown, null, null);

    public bool IsAvailable => Availability == AutoModeAvailability.Available;
}

/// <summary>
/// Which <c>--permission-mode</c> a run should actually use, and why (plan.md §5.3.2, §11.4c).
/// </summary>
/// <param name="AllowedTools">
/// The <c>--allowedTools</c> value to pass alongside <paramref name="Effective"/>. Broadened when
/// the run falls back to <c>acceptEdits</c>, where the list is what keeps the run from stopping.
/// </param>
public sealed record PermissionModeDecision(
    PermissionMode Configured,
    PermissionMode Effective,
    string AllowedTools,
    AutoModeStatus AutoMode,
    string? Explanation)
{
    /// <summary>True when the run will not use the configured mode — show the amber pill.</summary>
    public bool IsFallback => Effective != Configured;
}

/// <summary>
/// Probes <c>claude auto-mode config</c> once, caches the answer, and turns the configured
/// permission mode into the one a run should really use (plan.md §5.3.2's availability ladder).
/// </summary>
/// <remarks>
/// <para>
/// The ladder, top rung first:
/// </para>
/// <list type="number">
///   <item><description>
///     <c>auto</c> — everything runs, a classifier blocks destructive actions. Requires a supported
///     model and, on Team/Enterprise plans, Owner enablement, so it is never assumed: exit code 0
///     <b>and</b> parseable JSON from the probe is the only evidence accepted.
///   </description></item>
///   <item><description>
///     <c>acceptEdits</c> plus a broad <c>--allowedTools</c> list — the automatic fallback. Without
///     the broad list this rung is worse than useless: <c>acceptEdits</c> only auto-approves edits,
///     so the first <c>dotnet build</c> would stop and wait for a human.
///   </description></item>
///   <item><description>
///     <c>bypassPermissions</c> — <b>only</b> when explicitly opted in, by configuring it or by
///     enabling <see cref="PilotSettings.SkipPermissionsFallback"/> (which passes
///     <c>--dangerously-skip-permissions</c>, and that flag <em>implies</em> bypassPermissions).
///     Never reached by falling back.
///   </description></item>
/// </list>
/// <para>
/// The result carries the mode actually chosen so the runner can record it on
/// <c>RunRecord.PermissionModeUsed</c> and History can show which mode a run really used
/// (plan.md §11.4c).
/// </para>
/// </remarks>
public sealed class AutoModeProbe
{
    /// <summary>The subcommand plan.md §5.3.2 prescribes as the availability probe.</summary>
    public static readonly IReadOnlyList<string> ProbeArguments = ["auto-mode", "config"];

    /// <summary>A local config read; anything slower than this means the CLI is wedged.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The list used on the <c>acceptEdits</c> rung. Deliberately wider than
    /// <see cref="PilotSettings.DefaultAllowedTools"/>: under <c>auto</c> the list is redundant, but
    /// here it is the only thing standing between the run and a permission prompt.
    /// </summary>
    public const string BroadAllowedTools =
        "Bash,Read,Edit,Write,MultiEdit,NotebookEdit,Glob,Grep,LS,WebFetch,WebSearch,Task,TodoWrite,Skill";

    static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    readonly IAutoModeProbeRunner _runner;
    readonly ILogger<AutoModeProbe> _logger;
    readonly TimeProvider _timeProvider;
    readonly string _executable;
    readonly SemaphoreSlim _gate = new(1, 1);

    AutoModeStatus _status = AutoModeStatus.NotProbed;

    public AutoModeProbe(
        IAutoModeProbeRunner runner,
        ILogger<AutoModeProbe> logger,
        string executable = "claude",
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);

        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _executable = executable;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The cached answer without probing. <see cref="AutoModeAvailability.Unknown"/> until the first probe.</summary>
    public AutoModeStatus Status => Volatile.Read(ref _status);

    /// <summary>Probes once and caches; later calls return the cached status.</summary>
    public async Task<AutoModeStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (Status is { Availability: not AutoModeAvailability.Unknown } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A caller that queued behind an in-flight probe must not run a second one.
            if (_status is { Availability: not AutoModeAvailability.Unknown } raced)
            {
                return raced;
            }

            return await ProbeAndCacheAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Discards the cache and probes again — the "re-probe" button in Settings (plan.md Phase 3).
    /// Auto mode can be switched on by an Owner, or a model change can make it available, without
    /// anything about this app changing.
    /// </summary>
    public async Task<AutoModeStatus> ReprobeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ProbeAndCacheAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Applies the ladder to <paramref name="settings"/>, probing only when the answer can change
    /// the outcome (that is, only when <c>auto</c> is what was asked for).
    /// </summary>
    public async Task<PermissionModeDecision> DecideAsync(
        PilotSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var configured = settings.PermissionMode;
        var allowedTools = string.IsNullOrWhiteSpace(settings.AllowedTools)
            ? PilotSettings.DefaultAllowedTools
            : settings.AllowedTools.Trim();

        // `--dangerously-skip-permissions` implies bypassPermissions, so with it on that *is* the
        // mode the run uses, whatever the dropdown says. Recording anything else would put a lie
        // in the run history (plan.md §11.4c).
        if (settings.SkipPermissionsFallback)
        {
            return new PermissionModeDecision(
                configured,
                PermissionMode.BypassPermissions,
                allowedTools,
                Status,
                configured == PermissionMode.BypassPermissions
                    ? null
                    : "`Trust fallback: skip permissions entirely` is on, and " +
                      "`--dangerously-skip-permissions` implies bypassPermissions.");
        }

        if (configured != PermissionMode.Auto)
        {
            // acceptEdits chosen deliberately still needs the broad list, for the same reason the
            // fallback rung does.
            return new PermissionModeDecision(
                configured,
                configured,
                configured == PermissionMode.AcceptEdits ? Broaden(allowedTools) : allowedTools,
                Status,
                null);
        }

        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.IsAvailable)
        {
            return new PermissionModeDecision(
                PermissionMode.Auto, PermissionMode.Auto, allowedTools, status, null);
        }

        // Never fall back to bypassPermissions: that rung has no classifier and must stay an
        // explicit, deliberate opt-in (plan.md §11.4).
        return new PermissionModeDecision(
            PermissionMode.Auto,
            PermissionMode.AcceptEdits,
            Broaden(allowedTools),
            status,
            $"Auto mode is unavailable ({status.Detail ?? "no reason reported"}); " +
            "falling back to acceptEdits with a broad allowed-tools list.");
    }

    /// <summary>Union of the configured list and <see cref="BroadAllowedTools"/>, order preserved.</summary>
    public static string Broaden(string? allowedTools)
    {
        var merged = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tool in Split(allowedTools).Concat(Split(BroadAllowedTools)))
        {
            if (seen.Add(tool))
            {
                merged.Add(tool);
            }
        }

        return string.Join(',', merged);

        static IEnumerable<string> Split(string? list) => (list ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    async Task<AutoModeStatus> ProbeAndCacheAsync(CancellationToken cancellationToken)
    {
        var status = await ProbeAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _status, status);

        if (status.IsAvailable)
        {
            _logger.LogInformation("Auto permission mode is available.");
        }
        else
        {
            _logger.LogWarning("Auto permission mode is unavailable: {Detail}", status.Detail);
        }

        return status;
    }

    async Task<AutoModeStatus> ProbeAsync(CancellationToken cancellationToken)
    {
        AutoModeProbeOutput output;
        try
        {
            output = await _runner
                .RunAsync(_executable, ProbeArguments, ProbeTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Unavailable($"`{_executable} auto-mode config` could not be run: {FirstLine(ex.Message)}");
        }

        if (!output.Started)
        {
            return Unavailable($"`{_executable}` could not be started.");
        }

        if (output.TimedOut)
        {
            return Unavailable(
                $"`{_executable} auto-mode config` did not finish within {ProbeTimeout.TotalSeconds:0} seconds.");
        }

        if (output.ExitCode != 0)
        {
            var detail = FirstLine(output.StandardError);
            return Unavailable(detail.Length == 0
                ? $"`{_executable} auto-mode config` exited with code {output.ExitCode}."
                : $"`{_executable} auto-mode config` exited with code {output.ExitCode}: {detail}");
        }

        // Exit code 0 is not enough on its own: an older CLI can print help text for an unknown
        // subcommand and still exit 0. Parseable JSON is the second half of the plan.md §5.3.2 test.
        if (!TryParseJson(output.StandardOutput))
        {
            return Unavailable($"`{_executable} auto-mode config` did not print JSON.");
        }

        return new AutoModeStatus(AutoModeAvailability.Available, null, _timeProvider.GetUtcNow());
    }

    AutoModeStatus Unavailable(string detail) =>
        new(AutoModeAvailability.Unavailable, detail, _timeProvider.GetUtcNow());

    static bool TryParseJson(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        var start = output.IndexOfAny(['{', '[']);
        var end = output.LastIndexOfAny(['}', ']']);
        if (start < 0 || end <= start)
        {
            return false;
        }

        try
        {
            return JsonNode.Parse(output[start..(end + 1)], nodeOptions: null, DocumentOptions) is not null;
        }
        catch (JsonException)
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

/// <summary>
/// The real probe runner. Never throws for a missing CLI — a machine without Claude Code is an
/// expected state that preflight reports, not an exception the scheduler should have to catch.
/// </summary>
public sealed class AutoModeProbeRunner : IAutoModeProbeRunner
{
    public async Task<AutoModeProbeOutput> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
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

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                return new AutoModeProbeOutput(-1, string.Empty, string.Empty, Started: false);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new AutoModeProbeOutput(-1, string.Empty, ex.Message, Started: false);
        }

        // Anything that tries to read input must get EOF, not block forever (plan.md §5.3.3).
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
            return new AutoModeProbeOutput(-1, string.Empty, string.Empty, TimedOut: true);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            throw;
        }

        return new AutoModeProbeOutput(
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
