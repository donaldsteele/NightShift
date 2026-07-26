using NightShift.Core.Execution;

namespace NightShift.Core.Tests.Execution;

public sealed class RateLimitDetectorTests
{
    /// <summary>Epoch seconds 1785546000, as used in the fixtures below.</summary>
    static readonly DateTimeOffset ResetsAt = new(2026, 8, 1, 1, 0, 0, TimeSpan.Zero);

    static ClaudeStreamEvent Parse(string line) =>
        StreamJsonParser.ParseLine(line) ?? throw new InvalidOperationException("blank line");

    static string RateLimitLine(string status, double utilization, bool withReset = true)
    {
        var reset = withReset ? ",\"resetsAt\":1785546000" : string.Empty;
        return "{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"" + status +
               "\",\"rateLimitType\":\"five_hour\",\"utilization\":" +
               utilization.ToString(System.Globalization.CultureInfo.InvariantCulture) +
               reset + ",\"isUsingOverage\":false}}";
    }

    static string ResultLine(string resultText, string? apiErrorStatus = null)
    {
        var apiError = apiErrorStatus is null ? string.Empty : ",\"api_error_status\":\"" + apiErrorStatus + "\"";
        return "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"session_id\":\"s\"," +
               "\"total_cost_usd\":0.1,\"duration_ms\":10,\"result\":\"" + resultText + "\"" + apiError + "}";
    }

    [Theory]
    [InlineData("allowed")]
    [InlineData("allowed_warning")]
    [InlineData("ok")]
    public void A_healthy_rate_limit_event_is_not_an_encounter(string status)
    {
        var detector = new RateLimitDetector();

        detector.Observe(Parse(RateLimitLine(status, 0.36)));

        Assert.False(detector.WasRateLimited);
        Assert.Equal(ResetsAt, detector.LastKnownResetsAt);
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("blocked")]
    [InlineData("exceeded")]
    public void A_blocking_status_is_an_encounter_with_its_reset_time(string status)
    {
        var detector = new RateLimitDetector();

        detector.Observe(Parse(RateLimitLine(status, 1.0)));

        Assert.True(detector.WasRateLimited);
        Assert.Equal(RateLimitSignal.RateLimitEvent, detector.Encounter!.Signal);
        Assert.Equal("five_hour", detector.Encounter.RateLimitType);
        Assert.Equal(ResetsAt, detector.Encounter.ResetsAt);
    }

    [Fact]
    public void Full_utilization_is_an_encounter_even_when_the_status_looks_fine()
    {
        var detector = new RateLimitDetector();

        detector.Observe(Parse(RateLimitLine("allowed", 1.0)));

        Assert.True(detector.WasRateLimited);
    }

    [Fact]
    public void An_api_429_on_the_result_is_an_encounter()
    {
        var detector = new RateLimitDetector();

        detector.Observe(Parse(ResultLine("stopped", apiErrorStatus: "429")));

        Assert.True(detector.WasRateLimited);
        Assert.Equal(RateLimitSignal.ApiErrorStatus, detector.Encounter!.Signal);
    }

    [Fact]
    public void The_usage_limit_message_on_the_result_is_an_encounter()
    {
        var detector = new RateLimitDetector();

        detector.Observe(Parse(ResultLine("Claude usage limit reached. Your limit will reset at 3pm.")));

        Assert.True(detector.WasRateLimited);
        Assert.Equal(RateLimitSignal.ResultText, detector.Encounter!.Signal);
    }

    [Fact]
    public void A_usage_limit_on_stderr_is_an_encounter()
    {
        var detector = new RateLimitDetector();

        detector.ObserveStandardError("Claude usage limit reached.");

        Assert.True(detector.WasRateLimited);
        Assert.Equal(RateLimitSignal.StandardError, detector.Encounter!.Signal);
    }

    [Theory]
    // NightShift's own plan.md is full of these phrases. Running the pilot on this very repo must
    // not mark healthy runs as quota-blocked.
    [InlineData("Implemented the rate limit detector and its tests.")]
    [InlineData("Documented the 429 backoff ladder in plan.md section 4.1.")]
    [InlineData("Added CcusageProvider handling for the token limit field.")]
    [InlineData("The usage gate blocks runs above the threshold.")]
    [InlineData("Discussed rate limits, quotas and usage windows in the README.")]
    public void Talking_about_rate_limits_is_not_hitting_one(string resultText)
    {
        var detector = new RateLimitDetector();

        detector.Observe(Parse(ResultLine(resultText)));
        detector.ObserveStandardError(resultText);

        Assert.False(detector.WasRateLimited);
    }

    [Fact]
    public void The_strongest_signal_wins_but_a_reset_time_is_still_adopted()
    {
        var detector = new RateLimitDetector();

        // Weak signal first, without a reset time.
        detector.ObserveStandardError("Claude usage limit reached.");
        Assert.Equal(RateLimitSignal.StandardError, detector.Encounter!.Signal);

        // Then the structured one, which knows when the window clears.
        detector.Observe(Parse(RateLimitLine("rejected", 1.0)));

        Assert.Equal(RateLimitSignal.RateLimitEvent, detector.Encounter.Signal);
        Assert.Equal(ResetsAt, detector.Encounter.ResetsAt);
    }

    [Fact]
    public void A_reset_time_from_an_earlier_healthy_event_is_reused_when_the_limit_hits()
    {
        var detector = new RateLimitDetector();

        detector.Observe(Parse(RateLimitLine("allowed_warning", 0.9)));
        detector.Observe(Parse(ResultLine("Claude usage limit reached.")));

        Assert.True(detector.WasRateLimited);
        Assert.Equal(ResetsAt, detector.Encounter!.ResetsAt);
    }

    [Theory]
    [InlineData("Your limit will reset at 3pm (America/New_York)", "3pm (America/New_York)")]
    [InlineData("resets at 11:30 pm", "11:30 pm")]
    [InlineData("nothing here", null)]
    public void A_prose_reset_hint_is_extracted_but_never_parsed_into_a_timestamp(string text, string? expected) =>
        Assert.Equal(expected, RateLimitDetector.ExtractResetHint(text));
}
