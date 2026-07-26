using ClaudePilot.Core.Configuration;
using Serilog;
using Serilog.Events;

namespace ClaudePilot.Core.Logging;

/// <summary>
/// Builds the Serilog pipeline used by every ClaudePilot host. Kept in Core so a future CLI host
/// logs identically to the desktop app.
/// </summary>
public static class LoggingSetup
{
    public static LoggerConfiguration CreateConfiguration(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        paths.EnsureCreated();

        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: paths.LogFileTemplate,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                restrictedToMinimumLevel: LogEventLevel.Information,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                shared: true);
    }

    /// <summary>Installs the pipeline as the global <see cref="Log.Logger"/>.</summary>
    public static void Initialize(AppPaths paths) =>
        Log.Logger = CreateConfiguration(paths).CreateLogger();
}
