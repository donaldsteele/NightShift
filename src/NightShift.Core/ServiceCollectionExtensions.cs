using NightShift.Core.Configuration;
using NightShift.Core.History;
using NightShift.Core.Startup;
using NightShift.Core.Usage;
using Microsoft.Extensions.DependencyInjection;

namespace NightShift.Core;

/// <summary>
/// Single composition entry point for everything in NightShift.Core, so the Avalonia app and any
/// future CLI/service host register the same graph.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNightShiftCore(this IServiceCollection services, AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);

        services.AddSingleton(paths);
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<RunHistoryStore>();
        services.AddHostedService<StartupTasks>();

        AddUsage(services);

        return services;
    }

    /// <summary>
    /// Usage detection (plan.md §4). Both concrete providers are registered as
    /// <see cref="IUsageProvider"/> so <see cref="CompositeUsageProvider"/> receives them; the
    /// composite itself is resolved by its own type, because injecting it as an
    /// <see cref="IUsageProvider"/> would make it a member of its own provider list.
    /// </summary>
    static void AddUsage(IServiceCollection services)
    {
        services.AddSingleton<IClaudeCredentialReader, ClaudeCredentialReader>();
        services.AddSingleton<IClaudeVersionProvider, ClaudeVersionProvider>();
        services.AddSingleton<ICcusageProcessRunner, CcusageProcessRunner>();

        // A named client keeps the OAuth calls on their own handler lifetime; the endpoint is
        // undocumented and rate-limited, so it must never share a pool with anything chatty.
        services.AddHttpClient<OAuthUsageProvider>();

        services.AddSingleton<IUsageProvider>(sp => sp.GetRequiredService<OAuthUsageProvider>());
        services.AddSingleton<CcusageProvider>();
        services.AddSingleton<IUsageProvider>(sp => sp.GetRequiredService<CcusageProvider>());

        services.AddSingleton<CompositeUsageProvider>();
    }
}
