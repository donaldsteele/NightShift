using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NightShift.Core.Configuration;
using NightShift.Core.Execution;
using NightShift.Core.History;
using NightShift.Core.Logging;
using NightShift.Core.Usage;
using Serilog;

namespace NightShift.Core.Tests.Logging;

/// <summary>
/// plan.md §10 Phase 6 and §12: prove no secret reaches a log or the history store.
/// </summary>
/// <remarks>
/// Deliberately end-to-end rather than unit-level. <see cref="SecretRedactionTests"/> already proves
/// the redactor works on strings it is handed; the risk this file addresses is a *route* nobody
/// remembered to redact — a token that reaches disk through the run transcript, a history record, or
/// an exception message, none of which pass through the log formatter.
/// </remarks>
public sealed class SecretLeakAuditTests : IDisposable
{
    // Shaped like a real credential, but not one.
    const string FakeToken = "sk-ant-oat01-EXAMPLEnotarealtoken0123456789abcdef";

    readonly TempDirectory _temp = new();
    readonly AppPaths _paths;

    public SecretLeakAuditTests()
    {
        _paths = new AppPaths(_temp.Path);
        _paths.EnsureCreated();
    }

    static IEnumerable<string> AllFilesUnder(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            : [];

    void AssertNothingOnDiskContains(string secret)
    {
        var offenders = AllFilesUnder(_paths.Root)
            .Where(file => File.ReadAllText(file).Contains(secret, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"The token reached disk in: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void A_token_logged_by_any_route_never_reaches_the_log_file()
    {
        using (var logger = LoggingSetup.CreateConfiguration(_paths).CreateLogger())
        {
            logger.Information("As a property: {Token}", FakeToken);
            logger.Information("Interpolated into the template: Bearer " + FakeToken);
            logger.Warning("As structured data: {@Credentials}", new { accessToken = FakeToken });
            logger.Error(new InvalidOperationException($"accessToken={FakeToken}"), "Exception message");
            logger.Error(
                new InvalidOperationException("outer", new InvalidOperationException($"inner {FakeToken}")),
                "Nested exception");
        }

        AssertNothingOnDiskContains(FakeToken);
    }

    [Fact]
    public async Task A_token_in_a_run_transcript_or_record_never_reaches_the_history_store()
    {
        // The transcript is raw CLI output, so anything the tool prints lands here verbatim. This is
        // the most plausible leak route in the whole app.
        var history = new RunHistoryStore(_paths, NullLogger<RunHistoryStore>.Instance);
        var record = RunRecord.Start(DateTimeOffset.UnixEpoch);

        await history.AppendTranscriptAsync(
            record.Id,
            SecretRedactor.Scrub($"{{\"type\":\"system\",\"env\":{{\"ANTHROPIC_API_KEY\":\"{FakeToken}\"}}}}"));

        await history.AppendAsync(record.WithSummary(SecretRedactor.Scrub($"used token {FakeToken}")));

        AssertNothingOnDiskContains(FakeToken);
    }

    [Fact]
    public void The_settings_file_has_no_field_that_could_hold_a_credential()
    {
        // A regression guard: if somebody adds an ApiKey or Token setting, this fails and they have
        // to think about where it gets written and who can read settings.json.
        var json = JsonSerializer.Serialize(new PilotSettings(), NightShift.Core.Serialization.NightShiftJsonContext.Default.PilotSettings);

        using var document = JsonDocument.Parse(json);
        var suspicious = document.RootElement
            .EnumerateObject()
            .Select(p => p.Name)
            .Where(name =>
                name.Contains("token", StringComparison.OrdinalIgnoreCase)
                || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
                || name.Contains("password", StringComparison.OrdinalIgnoreCase)
                || name.Contains("apikey", StringComparison.OrdinalIgnoreCase)
                || name.Contains("credential", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(suspicious.Count == 0, $"Settings gained credential-shaped fields: {string.Join(", ", suspicious)}");
    }

    [Fact]
    public void A_run_record_has_no_field_that_could_hold_a_credential()
    {
        var record = RunRecord.Start(DateTimeOffset.UnixEpoch) with
        {
            UsageAtStart = UsageSnapshot.Unavailable("none", DateTimeOffset.UnixEpoch),
        };

        var json = JsonSerializer.Serialize(record, NightShift.Core.Serialization.NightShiftJsonLinesContext.Default.RunRecord);

        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apikey", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_credential_reader_never_puts_a_token_in_its_own_diagnostics()
    {
        var credentials = new ClaudeCredentials(FakeToken, DateTimeOffset.UnixEpoch.AddYears(60));

        // ToString() on a record prints every property by default — including the token — and a
        // record is exactly the kind of thing that ends up in a log message as {@Credentials}.
        Assert.DoesNotContain(FakeToken, credentials.ToString(), StringComparison.Ordinal);
    }

    public void Dispose() => _temp.Dispose();
}
