using ClaudePilot.Core.Configuration;
using ClaudePilot.Core.History;
using ClaudePilot.Core.Startup;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudePilot.Core;

/// <summary>
/// Single composition entry point for everything in ClaudePilot.Core, so the Avalonia app and any
/// future CLI/service host register the same graph.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddClaudePilotCore(this IServiceCollection services, AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);

        services.AddSingleton(paths);
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<RunHistoryStore>();
        services.AddHostedService<StartupTasks>();

        return services;
    }
}
