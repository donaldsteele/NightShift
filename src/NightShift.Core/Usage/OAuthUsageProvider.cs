using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NightShift.Core.Usage;

/// <summary>
/// The primary usage source: Anthropic's undocumented OAuth usage endpoint, which reports a true
/// percentage of the subscription's rate-limit quota (plan.md §4.1).
/// </summary>
/// <remarks>
/// Everything here exists because the endpoint is community-discovered and can change or vanish
/// without notice (plan.md §4.1 "Caveats to code against"):
/// <list type="bullet">
/// <item>Unknown fields are ignored and missing fields tolerated; a parse failure is reported, never thrown.</item>
/// <item><c>401</c> latches the provider unhealthy against *that token* — no retry loop — and asks
/// the user to re-authenticate. A different token clears the latch automatically.</item>
/// <item><c>429</c> backs off 1m → 5m → 15m, capped at 30m, and reports unavailable meanwhile.
/// There are known reports of persistent 429s; nothing here may spin.</item>
/// <item>Successful responses are cached for <see cref="CacheDuration"/> so a manual refresh next to
/// a scheduler tick cannot double-call.</item>
/// </list>
/// <see cref="IsHealthy"/>, <see cref="LastError"/>, <see cref="RequiresReauthentication"/> and
/// <see cref="BackoffUntil"/> are the state the dashboard banner reads (plan.md §9.1).
/// The <see cref="HttpClient"/> is injected and not owned — DI decides its lifetime and timeout.
/// </remarks>
public sealed class OAuthUsageProvider : IUsageProvider
{
    /// <summary>Exact endpoint from plan.md §4.1.</summary>
    public const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";

    /// <summary>Value of the mandatory <c>anthropic-beta</c> header.</summary>
    public const string AnthropicBetaValue = "oauth-2025-04-20";

    /// <summary>Product half of <c>User-Agent: claude-code/&lt;version&gt;</c>.</summary>
    public const string UserAgentProduct = "claude-code";

    /// <summary>Shown when the stored token's own expiry has passed (plan.md §4.1).</summary>
    public const string ExpiredCredentialMessage =
        "Claude login expired — run `claude` and re-authenticate.";

    /// <summary>Shown when the endpoint itself rejects the token.</summary>
    public const string UnauthorizedMessage =
        "Claude rejected the stored OAuth token (401) — run `claude` and re-authenticate.";

    /// <summary>Shown when no credential store could be read at all.</summary>
    public const string NoCredentialMessage =
        "No Claude OAuth credentials found — run `claude` and sign in.";

    /// <summary>Minimum lifetime of a successful response (plan.md §4.1: "at least 60 seconds").</summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    /// <summary>Ceiling for the 429 backoff, however many times we are throttled.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);

    /// <summary>1m, 5m, 15m, then <see cref="MaxBackoff"/> forever (plan.md §4.1).</summary>
    static readonly TimeSpan[] BackoffSchedule =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        MaxBackoff,
    ];

    readonly HttpClient _httpClient;
    readonly IClaudeCredentialReader _credentialReader;
    readonly IClaudeVersionProvider _versionProvider;
    readonly ILogger<OAuthUsageProvider> _logger;
    readonly TimeProvider _timeProvider;
    readonly SemaphoreSlim _gate = new(1, 1);

    UsageSnapshot? _cached;
    DateTimeOffset _cacheExpiresAt;
    int _consecutiveThrottles;
    string? _rejectedTokenFingerprint;

    long _backoffUntilTicks;
    volatile string? _lastError;
    volatile bool _isHealthy = true;

    public OAuthUsageProvider(
        HttpClient httpClient,
        IClaudeCredentialReader credentialReader,
        IClaudeVersionProvider versionProvider,
        ILogger<OAuthUsageProvider> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(credentialReader);
        ArgumentNullException.ThrowIfNull(versionProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _credentialReader = credentialReader;
        _versionProvider = versionProvider;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public UsageSource Source => UsageSource.OAuth;

    /// <summary>False once anything has failed; true again after the next successful call.</summary>
    public bool IsHealthy => _isHealthy;

    /// <summary>Why the last attempt failed, ready to put in a banner. Null while healthy.</summary>
    public string? LastError => _lastError;

    /// <summary>True when only a fresh `claude` login can fix things (expired token, or a 401).</summary>
    public bool RequiresReauthentication { get; private set; }

    /// <summary>When the 429 backoff lets us call again, or null when we are not backed off.</summary>
    public DateTimeOffset? BackoffUntil
    {
        get
        {
            var ticks = Interlocked.Read(ref _backoffUntilTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public async Task<UsageSnapshot> GetUsageAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await GetUsageCoreAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    async Task<UsageSnapshot> GetUsageCoreAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        // `forceRefresh` skips the cache and nothing else. The credential, latched-401 and 429 checks
        // below all still apply — a forced refresh must never turn into a retry into a wall.
        if (!forceRefresh && _cached is { } cached && now < _cacheExpiresAt)
        {
            return cached;
        }

        var credentials = await _credentialReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (credentials is null)
        {
            return Fail(NoCredentialMessage, now, requiresReauthentication: true);
        }

        if (credentials.IsExpiredAt(now))
        {
            // Do not spend a call we know will 401 — fall straight through to ccusage (plan.md §4.1).
            return Fail(ExpiredCredentialMessage, now, requiresReauthentication: true);
        }

        var fingerprint = Fingerprint(credentials.AccessToken);
        if (_rejectedTokenFingerprint is { } rejected)
        {
            if (string.Equals(rejected, fingerprint, StringComparison.Ordinal))
            {
                // The endpoint already told us this exact token is no good. Asking again in a loop is
                // the failure mode plan.md §4.1 forbids.
                return Fail(UnauthorizedMessage, now, requiresReauthentication: true);
            }

            // A new token appeared — the user re-authenticated, so give the endpoint another go.
            _logger.LogInformation("A new Claude OAuth token is present; clearing the unauthorized latch.");
            _rejectedTokenFingerprint = null;
            RequiresReauthentication = false;
        }

        if (BackoffUntil is { } until && now < until)
        {
            return Fail(ThrottleMessage(until - now), now, requiresReauthentication: false);
        }

        return await CallEndpointAsync(credentials, fingerprint, now, cancellationToken).ConfigureAwait(false);
    }

    async Task<UsageSnapshot> CallEndpointAsync(
        ClaudeCredentials credentials,
        string fingerprint,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var version = await _versionProvider.GetVersionAsync(cancellationToken).ConfigureAwait(false);

        using var request = BuildRequest(credentials.AccessToken, version);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return Fail($"Usage endpoint unreachable: {ex.Message}", now, requiresReauthentication: false, ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout, not our caller giving up.
            return Fail("Usage endpoint timed out.", now, requiresReauthentication: false, ex);
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized:
                    _rejectedTokenFingerprint = fingerprint;
                    return Fail(UnauthorizedMessage, now, requiresReauthentication: true);

                case HttpStatusCode.TooManyRequests:
                    return Throttled(response, now);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Fail(
                    $"Usage endpoint returned HTTP {(int)response.StatusCode} {response.StatusCode}.",
                    now,
                    requiresReauthentication: false);
            }

            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                return Fail($"Usage endpoint response could not be read: {ex.Message}", now, false, ex);
            }

            return Interpret(body, now);
        }
    }

    /// <summary>
    /// Builds the request exactly as plan.md §4.1 specifies. Headers go on the message rather than on
    /// the shared <see cref="HttpClient"/> so a client injected by DI is never mutated, and they are
    /// added without validation because a malformed stored token must produce a clean 401 from the
    /// server, not a <see cref="FormatException"/> here.
    /// </summary>
    static HttpRequestMessage BuildRequest(string accessToken, string version)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
        request.Headers.TryAddWithoutValidation("anthropic-beta", AnthropicBetaValue);
        request.Headers.TryAddWithoutValidation("User-Agent", $"{UserAgentProduct}/{version}");

        // `Content-Type` is a *content* header: HttpRequestMessage.Headers rejects it outright, so the
        // only way to send the header plan.md §4.1 mandates is to attach an empty body carrying it.
        var content = new ByteArrayContent([]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Content = content;

        return request;
    }

    UsageSnapshot Interpret(string body, DateTimeOffset now)
    {
        OAuthUsageResponseDto? payload;
        try
        {
            payload = JsonSerializer.Deserialize(body, OAuthUsageJsonContext.Default.OAuthUsageResponseDto);
        }
        catch (JsonException ex)
        {
            return Fail("Usage endpoint returned JSON we could not parse.", now, false, ex);
        }

        if (payload is null)
        {
            return Fail("Usage endpoint returned an empty body.", now, requiresReauthentication: false);
        }

        var fiveHour = ToWindow(payload.FiveHour);
        var sevenDay = ToWindow(payload.SevenDay);
        var sevenDayOpus = ToWindow(payload.SevenDayOpus);
        var sevenDaySonnet = ToWindow(payload.SevenDaySonnet);

        if (fiveHour is null && sevenDay is null && sevenDayOpus is null && sevenDaySonnet is null)
        {
            // Every window absent means the shape changed under us. Reporting "0% used" here would let
            // the scheduler start a run on a fabricated number — the worst failure mode (plan.md §4.3).
            return Fail(
                "Usage endpoint returned no recognisable rate-limit windows.",
                now,
                requiresReauthentication: false);
        }

        var snapshot = new UsageSnapshot(
            fiveHour,
            sevenDay,
            sevenDayOpus,
            sevenDaySonnet,
            UsageSource.OAuth,
            IsApproximate: false,
            now);

        _cached = snapshot;
        _cacheExpiresAt = now + CacheDuration;
        _consecutiveThrottles = 0;
        Interlocked.Exchange(ref _backoffUntilTicks, 0);
        RequiresReauthentication = false;
        _lastError = null;
        _isHealthy = true;

        return snapshot;
    }

    /// <summary>
    /// A window with no usable <c>utilization</c> is *absent*, not zero. plan.md §4.1 says to treat a
    /// null window as 0, but <see cref="UsageSnapshot"/> models absence directly and a fabricated 0
    /// would read as "plenty of quota left" to the threshold check in plan.md §4.3.
    /// </summary>
    static UsageWindow? ToWindow(OAuthUsageWindowDto? dto)
    {
        if (dto?.Utilization is not { } utilization || double.IsNaN(utilization) || double.IsInfinity(utilization))
        {
            return null;
        }

        return new UsageWindow(utilization, ParseResetsAt(dto.ResetsAt));
    }

    static DateTimeOffset? ParseResetsAt(string? value) =>
        DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;

    UsageSnapshot Throttled(HttpResponseMessage response, DateTimeOffset now)
    {
        _consecutiveThrottles++;
        var index = Math.Min(_consecutiveThrottles - 1, BackoffSchedule.Length - 1);
        var delay = BackoffSchedule[index];

        if (RetryAfter(response, now) is { } hinted && hinted > delay)
        {
            delay = hinted;
        }

        if (delay > MaxBackoff)
        {
            delay = MaxBackoff;
        }

        var until = now + delay;
        Interlocked.Exchange(ref _backoffUntilTicks, until.UtcTicks);

        _logger.LogWarning(
            "Usage endpoint returned 429 ({Count} in a row); backing off until {Until:O}.",
            _consecutiveThrottles,
            until);

        return Fail(ThrottleMessage(delay), now, requiresReauthentication: false);
    }

    static TimeSpan? RetryAfter(HttpResponseMessage response, DateTimeOffset now) =>
        response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } when date > now => date - now,
            _ => null,
        };

    static string ThrottleMessage(TimeSpan remaining) =>
        $"Anthropic rate-limited the usage endpoint (429); retrying in about {Describe(remaining)}.";

    static string Describe(TimeSpan span)
    {
        var minutes = (int)Math.Ceiling(span.TotalMinutes);
        return minutes <= 1 ? "a minute" : $"{minutes} minutes";
    }

    UsageSnapshot Fail(
        string reason,
        DateTimeOffset now,
        bool requiresReauthentication,
        Exception? exception = null)
    {
        _lastError = reason;
        _isHealthy = false;
        RequiresReauthentication = requiresReauthentication;

        // Reasons are built from status codes and exception messages only — the access token never
        // reaches a log sink (plan.md §8).
        _logger.LogWarning(exception, "OAuth usage unavailable: {Reason}", reason);

        return UsageSnapshot.Unavailable(reason, now);
    }

    /// <summary>
    /// Identifies a token without keeping or exposing it, so a 401 can be latched against the exact
    /// credential that caused it and released the moment the user logs in again.
    /// </summary>
    static string Fingerprint(string accessToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessToken)));
}
