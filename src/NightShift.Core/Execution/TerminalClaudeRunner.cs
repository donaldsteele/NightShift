using System.Diagnostics;
using NightShift.Core.Configuration;
using NightShift.Core.History;
using Microsoft.Extensions.Logging;

namespace NightShift.Core.Execution;

/// <summary>Where the prompt was left for the user to paste (plan.md §5.5).</summary>
/// <param name="PromptFilePath">File written inside the project's <c>.nightshift</c> directory.</param>
/// <param name="ClipboardCopied">Whether the clipboard copy succeeded — it is best-effort.</param>
public sealed record TerminalHandoff(string PromptFilePath, bool ClipboardCopied);

/// <summary>Copies text to the OS clipboard. Injectable because there is no clipboard in Core.</summary>
public interface IClipboard
{
    Task<bool> TrySetTextAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>
/// Opens a visible, interactive Claude session so a human can watch and intervene (plan.md §5.5).
/// </summary>
/// <remarks>
/// Output cannot be captured in this mode, so the run is recorded as
/// <see cref="RunOutcome.Launched"/> with no transcript. The prompt is written to
/// <c>&lt;project&gt;/.nightshift/next-prompt.txt</c> and copied to the clipboard; typing into the
/// terminal is deliberately not attempted, because it is unreliable.
/// <para>
/// Trust matters here in a way it does not headlessly: the interactive UI really does show the
/// "do you trust the files in this folder?" dialog, so trust is applied first when configured.
/// </para>
/// </remarks>
public sealed class TerminalClaudeRunner : IClaudeRunner
{
    /// <summary>Directory created inside the project for hand-off files.</summary>
    public const string HandoffDirectoryName = ".nightshift";

    public const string PromptFileName = "next-prompt.txt";

    const string WindowsTerminal = "wt.exe";

    readonly IClaudeExecutableLocator _locator;
    readonly IPromptBuilder _promptBuilder;
    readonly AutoModeProbe _autoModeProbe;
    readonly WorkspaceTrustManager _trustManager;
    readonly RunHistoryStore _history;
    readonly IClipboard? _clipboard;
    readonly ILogger<TerminalClaudeRunner> _logger;
    readonly TimeProvider _timeProvider;
    readonly Func<ProcessStartInfo, bool> _startProcess;

    public TerminalClaudeRunner(
        IClaudeExecutableLocator locator,
        IPromptBuilder promptBuilder,
        AutoModeProbe autoModeProbe,
        WorkspaceTrustManager trustManager,
        RunHistoryStore history,
        ILogger<TerminalClaudeRunner> logger,
        IClipboard? clipboard = null,
        TimeProvider? timeProvider = null,
        Func<ProcessStartInfo, bool>? startProcess = null)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _promptBuilder = promptBuilder ?? throw new ArgumentNullException(nameof(promptBuilder));
        _autoModeProbe = autoModeProbe ?? throw new ArgumentNullException(nameof(autoModeProbe));
        _trustManager = trustManager ?? throw new ArgumentNullException(nameof(trustManager));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clipboard = clipboard;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startProcess = startProcess ?? DefaultStart;
    }

    public LaunchMode Mode => LaunchMode.VisibleTerminal;

    /// <summary>Details of the most recent hand-off, so the UI can toast about it.</summary>
    public TerminalHandoff? LastHandoff { get; private set; }

    public async Task<RunRecord> RunAsync(
        PilotSettings settings,
        IProgress<RunProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var record = RunRecord.Start(_timeProvider.GetUtcNow());

        var resolution = _locator.Locate(settings);
        if (!resolution.IsFound)
        {
            return await FailAsync(record, resolution.FailureReason ?? "Claude Code was not found.").ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(settings.ProjectDirectory) || !Directory.Exists(settings.ProjectDirectory))
        {
            return await FailAsync(record, $"Project directory not found: '{settings.ProjectDirectory}'.").ConfigureAwait(false);
        }

        var decision = await _autoModeProbe.DecideAsync(settings, cancellationToken).ConfigureAwait(false);

        // Unlike headless mode, the interactive UI genuinely does show the trust dialog, so this is
        // what keeps the window from opening on a prompt instead of a session.
        if (settings.AutoTrustProjectFolder)
        {
            var trust = await _trustManager.ApplyAsync(settings.ProjectDirectory, cancellationToken).ConfigureAwait(false);
            if (!trust.IsTrusted)
            {
                _logger.LogWarning(
                    "Could not pre-trust {Directory}; the terminal may open on the trust dialog. {Error}",
                    settings.ProjectDirectory,
                    trust.Error);
            }
        }

        var prompt = _promptBuilder.Build(settings);
        var handoff = await WriteHandoffAsync(settings.ProjectDirectory, prompt, cancellationToken).ConfigureAwait(false);
        LastHandoff = handoff;

        if (settings.DryRun)
        {
            var dryRun = record with
            {
                EndedAt = _timeProvider.GetUtcNow(),
                Outcome = RunOutcome.Skipped,
                SkipReason = SkipReason.DryRun,
                SkipDetail = DescribeLaunch(resolution.ExecutablePath, settings, decision),
                PermissionModeUsed = decision.Effective,
            };
            await _history.AppendAsync(dryRun, cancellationToken).ConfigureAwait(false);
            return dryRun;
        }

        if (!TryLaunch(resolution.ExecutablePath, settings.ProjectDirectory, decision, out var launchError))
        {
            return await FailAsync(record, launchError ?? "Could not open a terminal.").ConfigureAwait(false);
        }

        var launched = (record with
        {
            EndedAt = _timeProvider.GetUtcNow(),
            Outcome = RunOutcome.Launched,
            PermissionModeUsed = decision.Effective,
            SkipDetail = DescribeLaunch(resolution.ExecutablePath, settings, decision),
        }).WithSummary(
            $"Terminal launched. Prompt written to {handoff.PromptFilePath}" +
            (handoff.ClipboardCopied
                ? " and copied to the clipboard."
                : _clipboard is null
                    // "Copy failed" would be a lie when nothing ever tried: Core has no clipboard,
                    // so a host that does not supply one simply has this feature switched off.
                    ? "; no clipboard is available in this host, so paste it from there."
                    : "; the clipboard copy failed, so paste it from there."));

        await _history.AppendAsync(launched, cancellationToken).ConfigureAwait(false);
        return launched;
    }

    async Task<TerminalHandoff> WriteHandoffAsync(
        string projectDirectory,
        string prompt,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(projectDirectory, HandoffDirectoryName);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, PromptFileName);

        await Io.AtomicFile.WriteAllTextAsync(path, prompt, cancellationToken).ConfigureAwait(false);

        var copied = false;
        if (_clipboard is not null)
        {
            try
            {
                copied = await _clipboard.TrySetTextAsync(prompt, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Clipboard copy failed; the prompt file is still available.");
            }
        }

        return new TerminalHandoff(path, copied);
    }

    bool TryLaunch(
        string executablePath,
        string projectDirectory,
        PermissionModeDecision decision,
        out string? error)
    {
        error = null;
        var mode = decision.Effective.ToCliValue();

        // Windows Terminal first, so the session lands in the shell the user actually uses.
        var windowsTerminal = new ProcessStartInfo
        {
            FileName = WindowsTerminal,
            UseShellExecute = true,
            WorkingDirectory = projectDirectory,
        };
        windowsTerminal.ArgumentList.Add("-d");
        windowsTerminal.ArgumentList.Add(projectDirectory);
        windowsTerminal.ArgumentList.Add("cmd");
        windowsTerminal.ArgumentList.Add("/k");
        windowsTerminal.ArgumentList.Add($"{ProcessArguments.Quote(executablePath)} --permission-mode {mode}");

        if (_startProcess(windowsTerminal))
        {
            return true;
        }

        _logger.LogInformation("Windows Terminal was not available; falling back to cmd.exe.");

        var fallback = new ProcessStartInfo
        {
            FileName = ProcessArguments.CommandShellFileName,
            UseShellExecute = true,
            WorkingDirectory = projectDirectory,
            Arguments = $"/k {ProcessArguments.Quote(executablePath)} --permission-mode {mode}",
        };

        if (_startProcess(fallback))
        {
            return true;
        }

        error = "Neither Windows Terminal nor cmd.exe could be started.";
        return false;
    }

    static string DescribeLaunch(string executablePath, PilotSettings settings, PermissionModeDecision decision) =>
        $"{ProcessArguments.Quote(executablePath)} --permission-mode {decision.Effective.ToCliValue()} " +
        $"(cwd {settings.ProjectDirectory})";

    bool DefaultStart(ProcessStartInfo startInfo)
    {
        try
        {
            using var process = Process.Start(startInfo);
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogDebug(ex, "Could not start {FileName}.", startInfo.FileName);
            return false;
        }
    }

    async Task<RunRecord> FailAsync(RunRecord record, string reason)
    {
        _logger.LogError("Terminal run {RunId} could not start: {Reason}", record.Id, reason);

        var failed = (record with
        {
            EndedAt = _timeProvider.GetUtcNow(),
            Outcome = RunOutcome.Failed,
            SkipDetail = reason,
        }).WithSummary(reason);

        await _history.AppendAsync(failed).ConfigureAwait(false);
        return failed;
    }
}
