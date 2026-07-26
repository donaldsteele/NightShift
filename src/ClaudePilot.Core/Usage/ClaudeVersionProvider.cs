using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ClaudePilot.Core.Usage;

/// <summary>
/// Supplies the version string for the mandatory <c>User-Agent: claude-code/&lt;version&gt;</c> header
/// (plan.md §4.1). Behind an interface so provider tests do not spawn a process.
/// </summary>
public interface IClaudeVersionProvider
{
    /// <summary>
    /// The installed Claude Code version, or a plausible fallback. Never throws and never returns
    /// empty: a missing CLI must not stop the usage endpoint being called with a valid User-Agent.
    /// </summary>
    ValueTask<string> GetVersionAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs <c>claude --version</c> once and caches the answer for the process lifetime (plan.md §4.1:
/// "Get the version once at startup by running <c>claude --version</c> and caching the result").
/// </summary>
/// <remarks>
/// The header is not cosmetic — without <c>claude-code/&lt;version&gt;</c> the endpoint drops the
/// caller into an aggressively rate-limited bucket, which is exactly the 429 spiral plan.md §4.1
/// warns about. So every failure path here still yields <see cref="FallbackVersion"/> rather than an
/// empty string, and the probe is bounded by <see cref="ProbeTimeout"/> so a wedged CLI cannot hang
/// a scheduler tick.
/// </remarks>
public sealed partial class ClaudeVersionProvider : IClaudeVersionProvider
{
    /// <summary>Used whenever the CLI cannot be run, is missing, or prints something unparsable.</summary>
    public const string FallbackVersion = "2.0.0";

    /// <summary>`claude --version` is a local, immediate command; longer than this means trouble.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    readonly ILogger<ClaudeVersionProvider> _logger;
    readonly string _executable;
    readonly SemaphoreSlim _gate = new(1, 1);

    string? _cached;

    public ClaudeVersionProvider(ILogger<ClaudeVersionProvider> logger, string executable = "claude")
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);

        _logger = logger;
        _executable = executable;
    }

    /// <summary>`claude --version` prints e.g. `2.0.14 (Claude Code)`; take the version token.</summary>
    [GeneratedRegex(@"\d+\.\d+(\.\d+)*(-[0-9A-Za-z.\-]+)?", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    public async ValueTask<string> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _cached) is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A second caller that queued behind the probe must not run it again.
            if (_cached is { } raced)
            {
                return raced;
            }

            var version = await ProbeAsync(cancellationToken).ConfigureAwait(false) ?? FallbackVersion;
            Volatile.Write(ref _cached, version);
            return version;
        }
        finally
        {
            _gate.Release();
        }
    }

    async Task<string?> ProbeAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        var startInfo = new ProcessStartInfo(_executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--version");

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                _logger.LogWarning("Could not start `{Executable} --version`; using {Fallback}.", _executable, FallbackVersion);
                return null;
            }

            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            var output = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                _logger.LogWarning(
                    "`{Executable} --version` exited {ExitCode}: {Error}",
                    _executable,
                    process.ExitCode,
                    error.Trim());
                return null;
            }

            var match = VersionPattern().Match(output);
            if (!match.Success)
            {
                _logger.LogWarning(
                    "`{Executable} --version` printed no recognisable version; using {Fallback}.",
                    _executable,
                    FallbackVersion);
                return null;
            }

            _logger.LogInformation("Claude Code version detected as {Version}.", match.Value);
            return match.Value;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "`{Executable} --version` did not finish within {Timeout}; using {Fallback}.",
                _executable,
                ProbeTimeout,
                FallbackVersion);
            TryKill(process);
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not run `{Executable} --version`; using {Fallback}.", _executable, FallbackVersion);
            return null;
        }
    }

    static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            // Already gone, or not ours to kill. Nothing useful left to do.
        }
    }
}
