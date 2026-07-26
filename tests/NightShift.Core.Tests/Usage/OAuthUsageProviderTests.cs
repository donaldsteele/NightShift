using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using NightShift.Core.Usage;
using Microsoft.Extensions.Logging.Abstractions;

namespace NightShift.Core.Tests.Usage;

public sealed class OAuthUsageProviderTests : IDisposable
{
    const string FullResponse = """
        {
          "five_hour":        { "utilization": 33.0, "resets_at": "2026-04-11T07:00:00.528743+00:00" },
          "seven_day":        { "utilization": 13.0, "resets_at": "2026-04-17T00:59:59.951713+00:00" },
          "seven_day_opus":   { "utilization": 4.5,  "resets_at": "2026-04-17T00:59:59.951713+00:00" },
          "seven_day_sonnet": { "utilization": 1.0,  "resets_at": "2026-04-16T03:00:00.951719+00:00" },
          "extra_usage":      { "is_enabled": false, "monthly_limit": null, "used_credits": null, "utilization": null }
        }
        """;

    static readonly DateTimeOffset Start = new(2026, 4, 11, 6, 0, 0, TimeSpan.Zero);

    readonly FakeHttpMessageHandler _handler = new();
    readonly HttpClient _httpClient;
    readonly StubCredentialReader _credentials = new();
    readonly StubTimeProvider _time = new(Start);

    public OAuthUsageProviderTests() => _httpClient = new HttpClient(_handler);

    OAuthUsageProvider CreateProvider(string version = "2.0.14") => new(
        _httpClient,
        _credentials,
        new StubVersionProvider(version),
        NullLogger<OAuthUsageProvider>.Instance,
        _time);

    // ── Parsing ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_full_response_populates_every_window()
    {
        _handler.Enqueue(HttpStatusCode.OK, FullResponse);
        var provider = CreateProvider();

        var snapshot = await provider.GetUsageAsync();

        Assert.True(snapshot.IsAvailable);
        Assert.Equal(UsageSource.OAuth, snapshot.Source);
        Assert.Equal(UsageSource.OAuth, provider.Source);
        Assert.False(snapshot.IsApproximate);
        Assert.Equal(Start, snapshot.RetrievedAt);
        Assert.Null(snapshot.UnavailableReason);

        Assert.Equal(33.0, snapshot.FiveHour?.UtilizationPercent);
        Assert.Equal(13.0, snapshot.SevenDay?.UtilizationPercent);
        Assert.Equal(4.5, snapshot.SevenDayOpus?.UtilizationPercent);
        Assert.Equal(1.0, snapshot.SevenDaySonnet?.UtilizationPercent);

        Assert.Equal(
            DateTimeOffset.Parse("2026-04-11T07:00:00.528743+00:00", CultureInfo.InvariantCulture),
            snapshot.FiveHour?.ResetsAt);

        Assert.True(provider.IsHealthy);
        Assert.Null(provider.LastError);
        Assert.False(provider.RequiresReauthentication);
        Assert.Null(provider.BackoffUntil);
    }

    [Fact]
    public async Task The_request_carries_exactly_the_headers_the_endpoint_requires()
    {
        _credentials.Credentials = new ClaudeCredentials("token-abc", null);
        _handler.Enqueue(HttpStatusCode.OK, FullResponse);

        await CreateProvider("9.9.9").GetUsageAsync();

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(OAuthUsageProvider.UsageEndpoint, request.Uri?.ToString());
        Assert.Equal("Bearer token-abc", request.Header("Authorization"));
        Assert.Equal("oauth-2025-04-20", request.Header("anthropic-beta"));
        Assert.Equal("claude-code/9.9.9", request.Header("User-Agent"));
        Assert.Equal("application/json", request.ContentType);
    }

    [Fact]
    public async Task A_null_window_is_absent_rather_than_a_fabricated_zero()
    {
        _handler.Enqueue(HttpStatusCode.OK, """
            {
              "five_hour":        { "utilization": 33.0, "resets_at": "2026-04-11T07:00:00.528743+00:00" },
              "seven_day":        null,
              "seven_day_opus":   null,
              "seven_day_sonnet": { "utilization": null, "resets_at": null }
            }
            """);

        var snapshot = await CreateProvider().GetUsageAsync();

        Assert.Equal(33.0, snapshot.FiveHour?.UtilizationPercent);
        Assert.Null(snapshot.SevenDay);
        Assert.Null(snapshot.SevenDayOpus);
        Assert.Null(snapshot.SevenDaySonnet);
    }

    [Fact]
    public async Task Unknown_fields_and_missing_windows_are_tolerated()
    {
        _handler.Enqueue(HttpStatusCode.OK, """
            {
              "five_hour": { "utilization": 7, "resets_at": "2026-04-11T07:00:00Z", "burn_rate": 1.2 },
              "something_anthropic_added_later": { "nested": [1, 2, 3] },
              "extra_usage": { "is_enabled": true, "monthly_limit": 40, "used_credits": 12 }
            }
            """);

        var snapshot = await CreateProvider().GetUsageAsync();

        Assert.True(snapshot.IsAvailable);
        Assert.Equal(7.0, snapshot.FiveHour?.UtilizationPercent);
        Assert.Null(snapshot.SevenDay);
        Assert.Null(snapshot.SevenDaySonnet);
    }

    [Fact]
    public async Task An_unparsable_reset_time_costs_only_the_reset_time()
    {
        _handler.Enqueue(HttpStatusCode.OK, """
            { "five_hour": { "utilization": 50, "resets_at": "in about an hour" } }
            """);

        var snapshot = await CreateProvider().GetUsageAsync();

        Assert.Equal(50.0, snapshot.FiveHour?.UtilizationPercent);
        Assert.Null(snapshot.FiveHour?.ResetsAt);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "five_hour": null, "seven_day": null }""")]
    [InlineData("""{ "five_hour": { "resets_at": "2026-04-11T07:00:00Z" } }""")]
    public async Task A_response_with_no_usable_window_is_unavailable_not_zero_percent(string body)
    {
        _handler.Enqueue(HttpStatusCode.OK, body);
        var provider = CreateProvider();

        var snapshot = await provider.GetUsageAsync();

        Assert.False(snapshot.IsAvailable);
        Assert.Contains("recognisable", snapshot.UnavailableReason!, StringComparison.Ordinal);
        Assert.False(provider.IsHealthy);
    }

    [Fact]
    public async Task Malformed_json_is_reported_not_thrown()
    {
        _handler.Enqueue(HttpStatusCode.OK, "<html>gateway error</html>");
        var provider = CreateProvider();

        var snapshot = await provider.GetUsageAsync();

        Assert.False(snapshot.IsAvailable);
        Assert.False(provider.IsHealthy);
        Assert.NotNull(provider.LastError);
    }

    // ── Credentials ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Expired_credentials_never_reach_the_network()
    {
        _credentials.Credentials = new ClaudeCredentials("stale", Start.AddMinutes(-1));
        var provider = CreateProvider();

        var snapshot = await provider.GetUsageAsync();

        Assert.Empty(_handler.Requests);
        Assert.False(snapshot.IsAvailable);
        Assert.Equal(OAuthUsageProvider.ExpiredCredentialMessage, snapshot.UnavailableReason);
        Assert.Contains("re-authenticate", snapshot.UnavailableReason!, StringComparison.Ordinal);
        Assert.True(provider.RequiresReauthentication);
        Assert.False(provider.IsHealthy);
    }

    [Fact]
    public async Task Absent_credentials_are_reported_without_a_call()
    {
        _credentials.Credentials = null;
        var provider = CreateProvider();

        var snapshot = await provider.GetUsageAsync();

        Assert.Empty(_handler.Requests);
        Assert.Equal(OAuthUsageProvider.NoCredentialMessage, snapshot.UnavailableReason);
        Assert.True(provider.RequiresReauthentication);
    }

    // ── Failure handling ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_401_marks_the_provider_unhealthy_and_is_never_retried_in_a_loop()
    {
        _handler.Enqueue(HttpStatusCode.Unauthorized);
        var provider = CreateProvider();

        var first = await provider.GetUsageAsync();
        _time.Advance(TimeSpan.FromHours(1));
        var second = await provider.GetUsageAsync();

        Assert.Single(_handler.Requests);
        Assert.Equal(OAuthUsageProvider.UnauthorizedMessage, first.UnavailableReason);
        Assert.Equal(OAuthUsageProvider.UnauthorizedMessage, second.UnavailableReason);
        Assert.Contains("re-authenticate", second.UnavailableReason!, StringComparison.Ordinal);
        Assert.False(provider.IsHealthy);
        Assert.True(provider.RequiresReauthentication);
    }

    [Fact]
    public async Task Re_authenticating_clears_the_401_latch()
    {
        _credentials.Credentials = new ClaudeCredentials("old-token", null);
        _handler.Enqueue(HttpStatusCode.Unauthorized);
        _handler.Enqueue(HttpStatusCode.OK, FullResponse);
        var provider = CreateProvider();

        await provider.GetUsageAsync();
        _credentials.Credentials = new ClaudeCredentials("brand-new-token", null);
        var snapshot = await provider.GetUsageAsync();

        Assert.Equal(2, _handler.Requests.Count);
        Assert.True(snapshot.IsAvailable);
        Assert.True(provider.IsHealthy);
        Assert.False(provider.RequiresReauthentication);
    }

    [Fact]
    public async Task A_429_backs_off_one_five_fifteen_then_caps_at_thirty_minutes()
    {
        for (var i = 0; i < 5; i++)
        {
            _handler.Enqueue(HttpStatusCode.TooManyRequests);
        }

        var provider = CreateProvider();
        TimeSpan[] expected =
        [
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(15),
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(30),
        ];

        for (var attempt = 0; attempt < expected.Length; attempt++)
        {
            var at = _time.GetUtcNow();

            var snapshot = await provider.GetUsageAsync();

            Assert.Equal(attempt + 1, _handler.Requests.Count);
            Assert.False(snapshot.IsAvailable);
            Assert.Contains("429", snapshot.UnavailableReason!, StringComparison.Ordinal);
            Assert.Equal(at + expected[attempt], provider.BackoffUntil);
            Assert.False(provider.IsHealthy);

            _time.Advance(expected[attempt] + TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task While_backed_off_the_endpoint_is_not_called_at_all()
    {
        _handler.Enqueue(HttpStatusCode.TooManyRequests);
        var provider = CreateProvider();

        await provider.GetUsageAsync();
        _time.Advance(TimeSpan.FromSeconds(59));
        var duringBackoff = await provider.GetUsageAsync();

        Assert.Single(_handler.Requests);
        Assert.False(duringBackoff.IsAvailable);
        Assert.Contains("429", duringBackoff.UnavailableReason!, StringComparison.Ordinal);

        _handler.Enqueue(HttpStatusCode.OK, FullResponse);
        _time.Advance(TimeSpan.FromSeconds(2));
        var afterBackoff = await provider.GetUsageAsync();

        Assert.Equal(2, _handler.Requests.Count);
        Assert.True(afterBackoff.IsAvailable);
        Assert.Null(provider.BackoffUntil);
    }

    [Fact]
    public async Task A_longer_retry_after_hint_wins_over_the_schedule()
    {
        _handler.Enqueue(
            HttpStatusCode.TooManyRequests,
            configure: response => response.Headers.RetryAfter =
                new RetryConditionHeaderValue(TimeSpan.FromMinutes(9)));

        var provider = CreateProvider();

        await provider.GetUsageAsync();

        Assert.Equal(Start.AddMinutes(9), provider.BackoffUntil);
    }

    [Fact]
    public async Task A_server_error_is_reported_and_retried_on_the_next_call()
    {
        _handler.Enqueue(HttpStatusCode.InternalServerError);
        _handler.Enqueue(HttpStatusCode.OK, FullResponse);
        var provider = CreateProvider();

        var failed = await provider.GetUsageAsync();

        Assert.False(failed.IsAvailable);
        Assert.Contains("500", failed.UnavailableReason!, StringComparison.Ordinal);
        Assert.False(provider.IsHealthy);
        Assert.False(provider.RequiresReauthentication);

        var recovered = await provider.GetUsageAsync();

        Assert.True(recovered.IsAvailable);
        Assert.True(provider.IsHealthy);
        Assert.Null(provider.LastError);
    }

    [Fact]
    public async Task A_network_failure_is_reported_not_thrown()
    {
        _handler.EnqueueThrow(new HttpRequestException("no such host"));
        var provider = CreateProvider();

        var snapshot = await provider.GetUsageAsync();

        Assert.False(snapshot.IsAvailable);
        Assert.Contains("unreachable", snapshot.UnavailableReason!, StringComparison.Ordinal);
    }

    // ── Caching ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_successful_response_is_cached_for_sixty_seconds()
    {
        _handler.Enqueue(HttpStatusCode.OK, FullResponse);
        var provider = CreateProvider();

        var first = await provider.GetUsageAsync();
        _time.Advance(TimeSpan.FromSeconds(59));
        var cached = await provider.GetUsageAsync();

        Assert.Single(_handler.Requests);
        Assert.Same(first, cached);
        Assert.Equal(1, _credentials.Reads);   // the cache hit did not even read the credential store

        _handler.Enqueue(HttpStatusCode.OK, FullResponse);
        _time.Advance(TimeSpan.FromSeconds(2));
        var refreshed = await provider.GetUsageAsync();

        Assert.Equal(2, _handler.Requests.Count);
        Assert.NotSame(first, refreshed);
        Assert.Equal(Start.AddSeconds(61), refreshed.RetrievedAt);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    // ── Test doubles ───────────────────────────────────────────────────────────────────────────

    /// <summary>Hand-written so the tests can assert on call counts and headers without a package.</summary>
    sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        readonly Queue<Func<HttpResponseMessage>> _responses = new();

        public List<CapturedRequest> Requests { get; } = [];

        public void Enqueue(
            HttpStatusCode status,
            string? body = null,
            Action<HttpResponseMessage>? configure = null) =>
            _responses.Enqueue(() =>
            {
                var response = new HttpResponseMessage(status)
                {
                    Content = new StringContent(body ?? string.Empty, Encoding.UTF8, "application/json"),
                };
                configure?.Invoke(response);
                return response;
            });

        public void EnqueueThrow(Exception exception) => _responses.Enqueue(() => throw exception);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(CapturedRequest.From(request));

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("The provider made an HTTP call the test did not expect.");
            }

            return Task.FromResult(_responses.Dequeue()());
        }
    }

    /// <summary>Headers are snapshotted at send time; the provider disposes the request afterwards.</summary>
    sealed record CapturedRequest(
        HttpMethod Method,
        Uri? Uri,
        IReadOnlyDictionary<string, string> Headers,
        string? ContentType)
    {
        public static CapturedRequest From(HttpRequestMessage request) => new(
            request.Method,
            request.RequestUri,
            request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase),
            request.Content?.Headers.ContentType?.ToString());

        public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
    }

    sealed class StubCredentialReader : IClaudeCredentialReader
    {
        public ClaudeCredentials? Credentials { get; set; } = new("default-test-token", null);

        public int Reads { get; private set; }

        public Task<ClaudeCredentials?> ReadAsync(CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(Credentials);
        }
    }

    sealed class StubVersionProvider : IClaudeVersionProvider
    {
        readonly string _version;

        public StubVersionProvider(string version) => _version = version;

        public ValueTask<string> GetVersionAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_version);
    }

    /// <summary>Manual clock: the 60s cache and the 429 backoff must be tested without waiting.</summary>
    sealed class StubTimeProvider : TimeProvider
    {
        DateTimeOffset _now;

        public StubTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
