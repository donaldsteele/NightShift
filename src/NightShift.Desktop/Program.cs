using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NightShift.Core;
using NightShift.Core.Configuration;
using NightShift.Core.Logging;
using NightShift.Desktop.Platform;
using NightShift.Desktop.Services;
using Serilog;

namespace NightShift.Desktop;

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
            // plan.md §9.4: one instance, because two would mean two schedulers, two run gates and
            // two writers racing over settings.json, state.json and runs/index.jsonl.
            using var guard = new SingleInstanceGuard();
            if (!guard.IsFirstInstance)
            {
                var surfaced = SecondInstanceSignal.TrySignalRunningInstance();
                Log.Information(
                    "NightShift is already running; {Outcome}.",
                    surfaced ? "asked it to show its window" : "could not reach it to surface its window");
                return 0;
            }

            using var host = BuildHost(paths, args, guard);
            App.Services = host.Services;

            host.Start();
            try
            {
                // Settings are on disk by now: StartupTasks loaded them during host.Start().
                var settings = host.Services.GetRequiredService<ISettingsStore>().Current;
                App.StartMinimizedToTray = settings.StartMinimized;

                using var signal = SecondInstanceSignal.Listen(App.SurfaceExistingWindow);

                Log.Information(
                    "NightShift starting. App data root: {Root}. Start minimized: {StartMinimized}.",
                    paths.Root,
                    settings.StartMinimized);

                return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            finally
            {
                host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "NightShift terminated unexpectedly");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    static IHost BuildHost(AppPaths paths, string[] args, SingleInstanceGuard guard)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(Log.Logger, dispose: false);

        builder.Services.AddNightShiftCore(paths);

        // The guard this process already owns, rather than a second one that would look at its own
        // mutex and conclude it had lost a race with itself.
        builder.Services.AddSingleton(guard);

        // Before AddNightShiftDesktop: its placeholders use TryAdd, so the first registration wins.
        App.RegisterPlatformServices(builder.Services);
        builder.Services.AddNightShiftDesktop();

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
