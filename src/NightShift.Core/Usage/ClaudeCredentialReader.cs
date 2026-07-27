using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NightShift.Core.Logging;

namespace NightShift.Core.Usage;

/// <summary>
/// The OAuth credential Claude Code stores after a browser login (plan.md §4.1).
/// </summary>
/// <param name="AccessToken">Bearer token for the usage endpoint. Never log this value.</param>
/// <param name="ExpiresAt">
/// Absolute expiry, converted from the file's epoch-**milliseconds** <c>expiresAt</c>. Null when the
/// credential store did not say — absence is not evidence of expiry, so it counts as still valid.
/// </param>
public sealed record ClaudeCredentials(string AccessToken, DateTimeOffset? ExpiresAt)
{
    /// <summary>True when <see cref="ExpiresAt"/> is known and already past.</summary>
    public bool IsExpired => IsExpiredAt(DateTimeOffset.UtcNow);

    /// <summary>Deterministic form of <see cref="IsExpired"/> for callers that own a clock.</summary>
    public bool IsExpiredAt(DateTimeOffset now) => ExpiresAt is { } expiry && expiry <= now;

    /// <summary>
    /// Redacted on purpose. A record's generated <c>ToString()</c> prints every property, so the
    /// default would put the access token into any log line, exception message or UI label that
    /// interpolates this object — routes that never pass through the log formatter's redactor.
    /// Found by <c>SecretLeakAuditTests</c>, which exists precisely to catch this class of leak.
    /// </summary>
    public override string ToString() =>
        $"ClaudeCredentials {{ AccessToken = {SecretRedactor.Placeholder}, ExpiresAt = {ExpiresAt:u} }}";

    /// <summary>Suppresses the compiler-generated member that would otherwise print the token.</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("AccessToken = ").Append(SecretRedactor.Placeholder);
        builder.Append(", ExpiresAt = ").Append(ExpiresAt?.ToString("u") ?? "null");
        return true;
    }
}

/// <summary>
/// Reads the local Claude Code OAuth credential. Behind an interface so
/// <see cref="OAuthUsageProvider"/> can be tested without a real login (plan.md §4.1).
/// </summary>
public interface IClaudeCredentialReader
{
    /// <summary>
    /// The stored credential, or null when there is none to be had — no login yet, an unreadable
    /// store, or a payload that does not contain <c>claudeAiOauth.accessToken</c>. Reading
    /// credentials must never throw: a missing login is an ordinary state for this app.
    /// </summary>
    Task<ClaudeCredentials?> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Default reader for the Claude Code credential store (plan.md §4.1). On Windows and Linux that is
/// <c>~/.claude/.credentials.json</c>; on macOS the same JSON lives in the login Keychain and is
/// fetched with <c>security find-generic-password</c>.
/// </summary>
/// <remarks>
/// The access token never reaches a log statement here — only paths, JSON shape problems and process
/// exit codes are logged, so a log file can be pasted into a bug report safely (plan.md §8).
/// An explicit <c>credentialsFilePath</c> always wins over platform detection; that is how tests
/// exercise the file path on any OS.
/// </remarks>
public sealed class ClaudeCredentialReader : IClaudeCredentialReader
{
    /// <summary>Keychain service name Claude Code stores its credential under.</summary>
    public const string KeychainService = "Claude Code-credentials";

    const string OAuthObjectName = "claudeAiOauth";
    const string AccessTokenName = "accessToken";
    const string ExpiresAtName = "expiresAt";

    /// <summary>The Keychain helper is local and fast; anything slower than this is a hang.</summary>
    public static readonly TimeSpan KeychainTimeout = TimeSpan.FromSeconds(5);

    readonly ILogger<ClaudeCredentialReader> _logger;
    readonly string? _credentialsFilePath;

    public ClaudeCredentialReader(ILogger<ClaudeCredentialReader> logger, string? credentialsFilePath = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (credentialsFilePath is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(credentialsFilePath);
        }

        _logger = logger;
        _credentialsFilePath = credentialsFilePath;
    }

    /// <summary><c>%USERPROFILE%\.claude\.credentials.json</c> / <c>~/.claude/.credentials.json</c>.</summary>
    public static string DefaultCredentialsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude",
        ".credentials.json");

    /// <summary>Where this instance will look, for diagnostics. Null when the Keychain is used.</summary>
    public string? CredentialsFilePath =>
        _credentialsFilePath ?? (UsesKeychain ? null : DefaultCredentialsFilePath);

    bool UsesKeychain => _credentialsFilePath is null && OperatingSystem.IsMacOS();

    public async Task<ClaudeCredentials?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var json = UsesKeychain
            ? await ReadFromKeychainAsync(cancellationToken).ConfigureAwait(false)
            : await ReadFromFileAsync(CredentialsFilePath!, cancellationToken).ConfigureAwait(false);

        return json is null ? null : Parse(json);
    }

    async Task<string?> ReadFromFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            _logger.LogInformation("No Claude credential file at {Path}; the user has not logged in here.", path);
            return null;
        }

        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read the Claude credential file at {Path}.", path);
            return null;
        }
    }

    async Task<string?> ReadFromKeychainAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(KeychainTimeout);

        var startInfo = new ProcessStartInfo("security")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("find-generic-password");
        startInfo.ArgumentList.Add("-s");
        startInfo.ArgumentList.Add(KeychainService);
        startInfo.ArgumentList.Add("-w");

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                _logger.LogWarning("Could not start `security` to read the Claude credential from the Keychain.");
                return null;
            }

            // The payload is the token itself, so stdout is read but never logged.
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            var output = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                _logger.LogWarning(
                    "`security find-generic-password` exited {ExitCode}: {Error}",
                    process.ExitCode,
                    error.Trim());
                return null;
            }

            return string.IsNullOrWhiteSpace(output) ? null : output;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("`security find-generic-password` did not finish within {Timeout}.", KeychainTimeout);
            TryKill(process);
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not run `security` to read the Claude credential from the Keychain.");
            return null;
        }
    }

    /// <summary>
    /// Pulls <c>claudeAiOauth.accessToken</c> / <c>expiresAt</c> out of the payload, tolerating extra
    /// fields and either a numeric or a stringified epoch. Anything unrecognisable yields null rather
    /// than an exception — a garbled credential store must degrade to "no login", not crash a run.
    /// </summary>
    ClaudeCredentials? Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "The Claude credential store did not contain valid JSON.");
            return null;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(OAuthObjectName, out var oauth) ||
                oauth.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning("The Claude credential store has no `{Property}` object.", OAuthObjectName);
                return null;
            }

            if (!oauth.TryGetProperty(AccessTokenName, out var tokenElement) ||
                tokenElement.ValueKind != JsonValueKind.String ||
                tokenElement.GetString() is not { Length: > 0 } token ||
                string.IsNullOrWhiteSpace(token))
            {
                _logger.LogWarning(
                    "The Claude credential store has no usable `{Property}.{Field}`.",
                    OAuthObjectName,
                    AccessTokenName);
                return null;
            }

            return new ClaudeCredentials(token, ReadExpiry(oauth));
        }
    }

    static DateTimeOffset? ReadExpiry(JsonElement oauth)
    {
        if (!oauth.TryGetProperty(ExpiresAtName, out var element))
        {
            return null;
        }

        var epochMilliseconds = element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(
                element.GetString(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => (long?)null,
        };

        if (epochMilliseconds is not { } milliseconds)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
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
