using NightShift.Core.Configuration;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace NightShift.Core.Logging;

/// <summary>
/// Builds the Serilog pipeline used by every NightShift host. Kept in Core so a future CLI host
/// logs identically to the desktop app — including the redaction, which is not optional.
/// </summary>
public static class LoggingSetup
{
    public const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    /// <summary>The formatter every NightShift sink must use: rendered output, then scrubbed.</summary>
    public static RedactingTextFormatter CreateFormatter() =>
        new(new MessageTemplateTextFormatter(OutputTemplate, formatProvider: null));

    public static LoggerConfiguration CreateConfiguration(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        paths.EnsureCreated();

        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                formatter: CreateFormatter(),
                path: paths.LogFileTemplate,
                restrictedToMinimumLevel: LogEventLevel.Information,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true);
    }

    /// <summary>Installs the pipeline as the global <see cref="Log.Logger"/>.</summary>
    public static void Initialize(AppPaths paths) =>
        Log.Logger = CreateConfiguration(paths).CreateLogger();
}
