using System.Text.RegularExpressions;

namespace NightShift.Core.Logging;

/// <summary>
/// Scrubs credential-shaped text before it can reach a log file (plan.md §8: the OAuth access token
/// must never appear in logs or the history store).
/// </summary>
/// <remarks>
/// Deliberately targeted rather than greedy. A catch-all "long opaque string" rule would also eat
/// git SHAs, run ids and file hashes, making logs useless for diagnosing exactly the failures this
/// app exists to survive.
/// </remarks>
public static partial class SecretRedactor
{
    public const string Placeholder = "***REDACTED***";

    /// <summary>Anthropic-issued keys and tokens: `sk-ant-...`.</summary>
    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_\-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex AnthropicKey();

    /// <summary>`Authorization: Bearer &lt;token&gt;` in any casing.</summary>
    [GeneratedRegex(@"(?i)\bbearer\s+[A-Za-z0-9._\-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex BearerToken();

    /// <summary>The credential fields of `~/.claude/.credentials.json`, quoted or not.</summary>
    [GeneratedRegex(
        @"(?i)""?(accessToken|refreshToken|access_token|refresh_token)""?\s*[:=]\s*""?[A-Za-z0-9._\-]{8,}""?",
        RegexOptions.CultureInvariant)]
    private static partial Regex CredentialField();

    /// <summary>Returns <paramref name="text"/> with every recognised secret replaced.</summary>
    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var scrubbed = AnthropicKey().Replace(text, Placeholder);
        scrubbed = BearerToken().Replace(scrubbed, $"Bearer {Placeholder}");
        scrubbed = CredentialField().Replace(scrubbed, match => $"{match.Groups[1].Value}={Placeholder}");

        return scrubbed;
    }
}
