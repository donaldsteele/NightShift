using Microsoft.Extensions.DependencyInjection;
using NightShift.Core.Configuration;
using NightShift.Core.Execution;
using NightShift.Core.History;
using NightShift.Core.Preflight;
using NightShift.Core.Scheduling;
using NightShift.Core.Startup;
using NightShift.Core.Usage;

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
        AddExecution(services);
        AddScheduling(services);

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

    /// <summary>
    /// Launching Claude Code (plan.md §5). Both runners are registered concretely and as
    /// <see cref="IClaudeRunner"/>; the scheduler picks between them by
    /// <see cref="Configuration.LaunchMode"/> at run time, because the setting can change without a
    /// restart.
    /// </summary>
    static void AddExecution(IServiceCollection services)
    {
        services.AddSingleton<IClaudeExecutableLocator, ClaudeExecutableLocator>();
        services.AddSingleton<IPromptBuilder, PromptBuilder>();
        services.AddSingleton<IClaudeProcessLauncher, ClaudeProcessLauncher>();
        services.AddSingleton<IAutoModeProbeRunner, AutoModeProbeRunner>();
        services.AddSingleton<AutoModeProbe>();
        services.AddSingleton<WorkspaceTrustManager>();
        services.AddSingleton<PermissionsAskScanner>();
        services.AddSingleton<IPreflightProcessRunner, PreflightProcessRunner>();
        services.AddSingleton<PreflightChecker>();
        services.AddSingleton<IPreflightChecker>(sp => sp.GetRequiredService<PreflightChecker>());

        services.AddSingleton<HeadlessClaudeRunner>();
        services.AddSingleton<TerminalClaudeRunner>();
        services.AddSingleton<IClaudeRunner>(sp => sp.GetRequiredService<HeadlessClaudeRunner>());
        services.AddSingleton<IClaudeRunner>(sp => sp.GetRequiredService<TerminalClaudeRunner>());
    }

    /// <summary>
    /// The background scheduler (plan.md §6). Registered both as itself and as the hosted service,
    /// so the UI can resolve the same instance it is watching — two instances would mean two timers
    /// and, worse, two run gates.
    /// </summary>
    static void AddScheduling(IServiceCollection services)
    {
        services.AddSingleton<RunGate>();
        services.AddSingleton<PilotStateStore>();
        services.AddSingleton<PilotScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<PilotScheduler>());
    }
}
