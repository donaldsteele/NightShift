using Avalonia;
using ClaudePilot.Core;
using ClaudePilot.Core.Configuration;
using ClaudePilot.Core.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace ClaudePilot.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        var paths = AppPaths.CreateDefault();
        LoggingSetup.Initialize(paths);

        try
        {
            using var host = BuildHost(paths, args);
            App.Services = host.Services;

            host.Start();
            try
            {
                Log.Information("ClaudePilot starting. App data root: {Root}", paths.Root);
                return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            finally
            {
                host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "ClaudePilot terminated unexpectedly");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    static IHost BuildHost(AppPaths paths, string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(Log.Logger, dispose: false);

        builder.Services.AddClaudePilotCore(paths);

        return builder.Build();
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
