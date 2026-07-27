using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace NightShift.Core.Execution;

/// <summary>
/// Opens a visible terminal running <c>claude</c> in a directory.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <see cref="TerminalClaudeRunner"/> because it has a second caller: the plan
/// window's "Edit with Claude", which is an <b>attended</b> session and therefore wants
/// <c>--permission-mode plan</c> — a mode the unattended path must never use, because plan mode
/// ends by waiting for a human to approve and there is nobody there at 3am.
/// </para>
/// <para>
/// <b>That is why <paramref name="cliMode"/> is a string.</b> <c>PermissionMode</c> deliberately has
/// no <c>Plan</c> value and <c>ToCliValue</c> throws on anything outside its three, which is the
/// guard that keeps plan mode out of the settings dropdown and out of the scheduler. Widening the
/// enum to serve this caller would dismantle exactly the protection it exists to give.
/// </para>
/// </remarks>
public interface IClaudeTerminalLauncher
{
    /// <summary>Opens the terminal. Returns false with a reason rather than throwing.</summary>
    /// <param name="cliMode">The literal <c>--permission-mode</c> value.</param>
    /// <param name="seedPrompt">
    /// An optional opening prompt, passed as <c>claude</c>'s positional argument. Dropped with a
    /// warning when it contains something <c>cmd.exe</c> would reinterpret — see
    /// <see cref="ProcessArguments.HasCommandShellHazard"/>.
    /// </param>
    /// <param name="extraArguments">
    /// Already-quoted flags appended verbatim, e.g. <c>--remote-control "name"</c>.
    /// </param>
    bool TryLaunch(
        string executablePath,
        string workingDirectory,
        string cliMode,
        string? seedPrompt,
        string? extraArguments,
        out string? error);
}

/// <summary>The real launcher: Windows Terminal, falling back to <c>cmd.exe</c>.</summary>
/// <remarks>
/// There is no macOS or Linux branch, and there never was — the app is <c>WinExe</c> and ships
/// win-x64 only. Adding one is a separate piece of work, not an oversight here.
/// </remarks>
public sealed class ClaudeTerminalLauncher : IClaudeTerminalLauncher
{
    const string WindowsTerminal = "wt.exe";

    readonly ILogger _logger;
    readonly Func<ProcessStartInfo, bool> _startProcess;

    /// <param name="logger">
    /// Bare <see cref="ILogger"/> rather than the generic, so <see cref="TerminalClaudeRunner"/>
    /// can hand over its own and keep one category for the whole launch. DI supplies the generic
    /// through an explicit factory registration.
    /// </param>
    /// <param name="startProcess">
    /// The seam tests launch through. Without it there is no way to assert what command line was
    /// built without actually opening a terminal on the build agent.
    /// </param>
    public ClaudeTerminalLauncher(
        ILogger logger,
        Func<ProcessStartInfo, bool>? startProcess = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _startProcess = startProcess ?? DefaultStart;
    }

    public bool TryLaunch(
        string executablePath,
        string workingDirectory,
        string cliMode,
        string? seedPrompt,
        string? extraArguments,
        out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(cliMode);

        error = null;

        var command = BuildCommand(executablePath, cliMode, seedPrompt, extraArguments);

        // Windows Terminal first, so the session lands in the shell the user actually uses.
        var windowsTerminal = new ProcessStartInfo
        {
            FileName = WindowsTerminal,
            UseShellExecute = true,
            WorkingDirectory = workingDirectory,
        };
        windowsTerminal.ArgumentList.Add("-d");
        windowsTerminal.ArgumentList.Add(workingDirectory);
        windowsTerminal.ArgumentList.Add("cmd");
        windowsTerminal.ArgumentList.Add("/k");
        windowsTerminal.ArgumentList.Add(command);

        if (_startProcess(windowsTerminal))
        {
            return true;
        }

        _logger.LogInformation("Windows Terminal was not available; falling back to cmd.exe.");

        var fallback = new ProcessStartInfo
        {
            FileName = ProcessArguments.CommandShellFileName,
            UseShellExecute = true,
            WorkingDirectory = workingDirectory,
            Arguments = $"/k {command}",
        };

        if (_startProcess(fallback))
        {
            return true;
        }

        error = "Neither Windows Terminal nor cmd.exe could be started.";
        return false;
    }

    /// <summary>
    /// The <c>claude …</c> command line handed to <c>cmd /k</c>.
    /// </summary>
    /// <remarks>
    /// Every piece that is concatenated here is checked with
    /// <see cref="ProcessArguments.HasCommandShellHazard"/> first. <c>cmd.exe</c> parses this line
    /// before <c>CommandLineToArgvW</c> ever sees it and the two disagree — <c>%VAR%</c> expands
    /// even inside quotes, and a <c>\"</c> means "literal quote" to one and "quoting off" to the
    /// other. This is that helper's first production caller; it was written, tested and then never
    /// wired up.
    /// </remarks>
    string BuildCommand(string executablePath, string cliMode, string? seedPrompt, string? extraArguments)
    {
        var command = $"{ProcessArguments.Quote(executablePath)} --permission-mode {cliMode}";

        if (extraArguments is { Length: > 0 })
        {
            command += extraArguments.StartsWith(' ') ? extraArguments : $" {extraArguments}";
        }

        if (seedPrompt is not { Length: > 0 })
        {
            return command;
        }

        if (ProcessArguments.HasCommandShellHazard(seedPrompt))
        {
            // Dropped rather than mangled: the session still opens, and the caller has already put
            // the full text on the clipboard.
            _logger.LogWarning(
                "The opening prompt was not passed to the terminal because cmd.exe would " +
                "reinterpret it. It is still on the clipboard.");
            return command;
        }

        return $"{command} {ProcessArguments.Quote(seedPrompt)}";
    }

    /// <summary>Starting a terminal that is not installed is an expected outcome, not an error.</summary>
    bool DefaultStart(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo) is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            or InvalidOperationException
            or IOException)
        {
            _logger.LogDebug(ex, "Could not start {FileName}.", startInfo.FileName);
            return false;
        }
    }
}
