using NightShift.Core.Usage;
using Microsoft.Extensions.Logging.Abstractions;

namespace NightShift.Core.Tests.Usage;

public sealed class ClaudeCredentialReaderTests : IDisposable
{
    readonly TempDirectory _temp = new();

    string CredentialsFile => Path.Combine(_temp.Path, ".credentials.json");

    ClaudeCredentialReader CreateReader() =>
        new(NullLogger<ClaudeCredentialReader>.Instance, CredentialsFile);

    async Task<ClaudeCredentials?> ReadAsync(string json)
    {
        await File.WriteAllTextAsync(CredentialsFile, json);
        return await CreateReader().ReadAsync();
    }

    [Fact]
    public async Task Reads_the_access_token_and_expiry()
    {
        var credentials = await ReadAsync("""
            {
              "claudeAiOauth": {
                "accessToken": "sk-ant-oat01-example-token",
                "expiresAt": 1700000000000
              }
            }
            """);

        Assert.NotNull(credentials);
        Assert.Equal("sk-ant-oat01-example-token", credentials.AccessToken);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000), credentials.ExpiresAt);
    }

    [Fact]
    public async Task Expiry_is_read_as_epoch_milliseconds_not_seconds()
    {
        var credentials = await ReadAsync("""
            { "claudeAiOauth": { "accessToken": "t", "expiresAt": 1700000000000 } }
            """);

        // 1_700_000_000_000 ms == 2023-11-14T22:13:20Z. Read as seconds it would land in 55854.
        Assert.Equal(new DateTimeOffset(2023, 11, 14, 22, 13, 20, TimeSpan.Zero), credentials?.ExpiresAt);
    }

    [Fact]
    public async Task An_expiry_in_the_past_is_expired()
    {
        var past = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();

        var credentials = await ReadAsync($$"""
            { "claudeAiOauth": { "accessToken": "t", "expiresAt": {{past}} } }
            """);

        Assert.True(credentials?.IsExpired);
    }

    [Fact]
    public async Task An_expiry_in_the_future_is_not_expired()
    {
        var future = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();

        var credentials = await ReadAsync($$"""
            { "claudeAiOauth": { "accessToken": "t", "expiresAt": {{future}} } }
            """);

        Assert.False(credentials?.IsExpired);
    }

    [Fact]
    public void An_unknown_expiry_is_never_treated_as_expired()
    {
        Assert.False(new ClaudeCredentials("t", ExpiresAt: null).IsExpired);
        Assert.False(new ClaudeCredentials("t", null).IsExpiredAt(DateTimeOffset.MaxValue));
    }

    [Fact]
    public void The_expiry_instant_itself_counts_as_expired()
    {
        var expiry = new DateTimeOffset(2026, 4, 11, 7, 0, 0, TimeSpan.Zero);
        var credentials = new ClaudeCredentials("t", expiry);

        Assert.True(credentials.IsExpiredAt(expiry));
        Assert.False(credentials.IsExpiredAt(expiry.AddTicks(-1)));
    }

    [Fact]
    public async Task A_stringified_expiry_is_accepted()
    {
        var credentials = await ReadAsync("""
            { "claudeAiOauth": { "accessToken": "t", "expiresAt": "1700000000000" } }
            """);

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000), credentials?.ExpiresAt);
    }

    [Fact]
    public async Task Missing_and_unknown_fields_are_tolerated()
    {
        var credentials = await ReadAsync("""
            {
              "claudeAiOauth": {
                "accessToken": "t",
                "refreshToken": "r",
                "scopes": ["user:inference", "user:profile"],
                "subscriptionType": "max",
                "somethingNew": { "nested": true }
              },
              "otherProvider": { "accessToken": "x" }
            }
            """);

        Assert.NotNull(credentials);
        Assert.Equal("t", credentials.AccessToken);
        Assert.Null(credentials.ExpiresAt);
    }

    [Fact]
    public async Task A_nonsensical_expiry_costs_only_the_expiry()
    {
        var credentials = await ReadAsync("""
            { "claudeAiOauth": { "accessToken": "t", "expiresAt": { "seconds": 12 } } }
            """);

        Assert.NotNull(credentials);
        Assert.Null(credentials.ExpiresAt);
    }

    [Fact]
    public async Task A_missing_file_yields_no_credentials()
    {
        Assert.False(File.Exists(CredentialsFile));

        Assert.Null(await CreateReader().ReadAsync());
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("""{ "somethingElse": 1 }""")]
    [InlineData("""{ "claudeAiOauth": "not an object" }""")]
    [InlineData("""{ "claudeAiOauth": { } }""")]
    [InlineData("""{ "claudeAiOauth": { "accessToken": "" } }""")]
    [InlineData("""{ "claudeAiOauth": { "accessToken": "   " } }""")]
    [InlineData("""{ "claudeAiOauth": { "accessToken": 12345 } }""")]
    public async Task An_unusable_credential_store_yields_null_rather_than_throwing(string json)
    {
        Assert.Null(await ReadAsync(json));
    }

    [Fact]
    public void The_default_path_is_the_claude_credentials_file_in_the_user_profile()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            ".credentials.json");

        Assert.Equal(expected, ClaudeCredentialReader.DefaultCredentialsFilePath);
        Assert.Equal(CredentialsFile, CreateReader().CredentialsFilePath);
    }

    [Fact]
    public void A_blank_path_override_is_rejected_outright()
    {
        Assert.Throws<ArgumentException>(
            () => new ClaudeCredentialReader(NullLogger<ClaudeCredentialReader>.Instance, "   "));
    }

    public void Dispose() => _temp.Dispose();
}
