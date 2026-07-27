using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace NightShift.Core.Execution;

/// <summary>How to start one child process.</summary>
/// <param name="FileName">Executable to launch.</param>
/// <param name="Arguments">Arguments, unquoted — the launcher does the quoting.</param>
/// <param name="WorkingDirectory">Directory the process runs in.</param>
public sealed record ClaudeProcessStart(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory)
{
    /// <summary>The command line as it would appear in a shell, for logs and dry runs.</summary>
    public string ToDisplayString() =>
        $"{ProcessArguments.Quote(FileName)} {ProcessArguments.BuildCommandLine(Arguments)}";
}

/// <summary>A running child process, reduced to what the runner actually needs.</summary>
public interface IClaudeProcess : IDisposable
{
    /// <summary>Standard output, to be consumed as newline-delimited JSON.</summary>
    TextReader StandardOutput { get; }

    /// <summary>Standard error, drained concurrently so a full pipe buffer cannot deadlock the child.</summary>
    TextReader StandardError { get; }

    /// <summary>Completes when the process exits, yielding its exit code.</summary>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Kills the process and every child it spawned.</summary>
    void KillTree();
}

/// <summary>Starts Claude Code. Injectable so runner logic is testable without spawning anything.</summary>
public interface IClaudeProcessLauncher
{
    IClaudeProcess Start(ClaudeProcessStart start);
}

/// <summary>
/// The real launcher.
/// </summary>
/// <remarks>
/// Launches the executable <b>directly</b>, including an npm <c>.cmd</c> shim, with
/// <c>UseShellExecute = false</c> and <see cref="ProcessStartInfo.ArgumentList"/> — never through
/// <c>cmd.exe /c</c>. Measured on .NET 10 / Windows 11: direct launch of a <c>.cmd</c> works, and
/// arguments reach the batch with Win32 escaping intact so its <c>%*</c> forwards them faithfully.
/// The <c>cmd.exe</c> route is lossy — it expands <c>%VAR%</c> even inside quotes and reinterprets
/// <c>\"</c> — and our prompt is user-editable free text that may contain both (plan.md §5.1).
/// <para>
/// stdin is redirected and closed immediately: anything that tries to read input gets EOF instead of
/// blocking forever in a run with no human present (plan.md §5.3.3).
/// </para>
/// </remarks>
public sealed class ClaudeProcessLauncher : IClaudeProcessLauncher, IDisposable
{
    readonly ILogger<ClaudeProcessLauncher> _logger;
    readonly ProcessJob? _job;

    public ClaudeProcessLauncher(ILogger<ClaudeProcessLauncher> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Created once and held for the app's lifetime. The kill guarantee comes from this handle
        // being closed — including by the OS when NightShift is terminated without warning.
        _job = OperatingSystem.IsWindows() ? ProcessJob.TryCreate(logger) : null;
    }

    public IClaudeProcess Start(ClaudeProcessStart start)
    {
        ArgumentNullException.ThrowIfNull(start);

        var startInfo = new ProcessStartInfo
        {
            FileName = start.FileName,
            WorkingDirectory = start.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in start.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();

        // Assign before the child gets far. Everything it spawns inherits the job, so a tool call
        // that starts its own long-running process is covered too.
        if (OperatingSystem.IsWindows())
        {
            _job?.TryAssign(process, _logger);
        }

        // EOF for anything that tries to prompt.
        process.StandardInput.Close();

        return new SystemClaudeProcess(process);
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            _job?.Dispose();
        }
    }

    sealed class SystemClaudeProcess(Process process) : IClaudeProcess
    {
        public TextReader StandardOutput => process.StandardOutput;

        public TextReader StandardError => process.StandardError;

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }

        public void KillTree()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone between the check and the kill.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Access denied killing a child we no longer own; nothing useful to do.
            }
        }

        public void Dispose() => process.Dispose();
    }
}
