using Serilog.Events;
using Serilog.Formatting;

namespace NightShift.Core.Logging;

/// <summary>
/// Wraps another formatter and scrubs its output with <see cref="SecretRedactor"/>.
/// </summary>
/// <remarks>
/// Redaction happens at the formatter, not in an enricher, on purpose: an enricher can only rewrite
/// property values, so a secret baked into a message template literal — or into an exception's
/// message or stack trace — would slip straight through to the file. Everything a sink writes goes
/// through here first.
/// </remarks>
public sealed class RedactingTextFormatter : ITextFormatter
{
    readonly ITextFormatter _inner;

    public RedactingTextFormatter(ITextFormatter inner) =>
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(output);

        using var buffer = new StringWriter();
        _inner.Format(logEvent, buffer);
        output.Write(SecretRedactor.Scrub(buffer.ToString()));
    }
}
