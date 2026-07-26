using NightShift.Core.Configuration;
using NightShift.Core.Logging;
using Serilog;

namespace NightShift.Core.Tests.Logging;

public sealed class SecretRedactionTests : IDisposable
{
    // Shaped like a real credential, but not one.
    const string FakeToken = "sk-ant-oat01-EXAMPLEnotarealtoken0123456789abcdef";

    readonly TempDirectory _temp = new();

    [Theory]
    [InlineData("Authorization: Bearer " + FakeToken)]
    [InlineData("{\"accessToken\":\"" + FakeToken + "\"}")]
    [InlineData("accessToken=" + FakeToken)]
    [InlineData("token is " + FakeToken + " ok")]
    public void Credential_shaped_text_is_scrubbed(string input)
    {
        var scrubbed = SecretRedactor.Scrub(input);

        Assert.DoesNotContain(FakeToken, scrubbed, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Placeholder, scrubbed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Run 20260726-120000-abc123 finished with exit code 0")]
    [InlineData(@"Project directory C:\src\my-project has no plan.md")]
    [InlineData("Commit 6f4d0c1e8a2b3c4d5e6f708192a3b4c5d6e7f809 built cleanly")]
    public void Ordinary_diagnostics_survive_untouched(string input) =>
        Assert.Equal(input, SecretRedactor.Scrub(input));

    [Fact]
    public void A_token_never_reaches_the_sink()
    {
        var paths = new AppPaths(_temp.Path);

        using (var logger = LoggingSetup.CreateConfiguration(paths).CreateLogger())
        {
            // Every route a secret could take into a log line.
            logger.Information("Raw literal in the template: {Token}", FakeToken);
            logger.Information("Header was Authorization: Bearer " + FakeToken);
            logger.Error(
                new InvalidOperationException($"credentials.json said accessToken={FakeToken}"),
                "Usage lookup failed");
        }

        var written = Directory.GetFiles(paths.LogsDirectory, "*.log")
            .Select(File.ReadAllText)
            .ToList();

        Assert.NotEmpty(written);
        Assert.All(written, text => Assert.DoesNotContain(FakeToken, text, StringComparison.Ordinal));
        Assert.Contains(written, text => text.Contains(SecretRedactor.Placeholder, StringComparison.Ordinal));
        Assert.Contains(written, text => text.Contains("Usage lookup failed", StringComparison.Ordinal));
    }

    public void Dispose() => _temp.Dispose();
}
