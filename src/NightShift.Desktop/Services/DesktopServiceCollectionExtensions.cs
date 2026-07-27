using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightShift.Core.Execution;
using NightShift.Desktop.ViewModels;

namespace NightShift.Desktop.Services;

/// <summary>
/// Registers everything the Avalonia app adds on top of <c>AddNightShiftCore</c>.
/// </summary>
/// <remarks>
/// <para>
/// Call after <c>AddNightShiftCore(paths)</c>: this fills in
/// <see cref="NightShift.Core.Execution.IClipboard"/>, which Core declares but cannot implement,
/// and adds the view models.
/// </para>
/// <para>
/// <b>Three registrations are placeholders</b> — <see cref="IFolderPicker"/>,
/// <see cref="IFilePicker"/> and <see cref="IConfirmationService"/> all need a
/// <see cref="Avalonia.Controls.TopLevel"/> that does not exist at host-build time. They are
/// registered with <c>TryAdd</c> so the view layer can replace them once a window exists, and every
/// placeholder fails closed: a pick is "cancelled", a confirmation is "no".
/// </para>
/// <para>
/// View models are singletons. There is one window, one scheduler and one settings store, and each
/// view model attaches event handlers to those — a transient registration would silently accumulate
/// subscriptions every time a view was rebuilt.
/// </para>
/// </remarks>
public static class DesktopServiceCollectionExtensions
{
    public static IServiceCollection AddNightShiftDesktop(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        AddPlatformServices(services);
        AddViewModels(services);

        return services;
    }

    static void AddPlatformServices(IServiceCollection services)
    {
        services.TryAddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.TryAddSingleton<IShellLauncher, ShellLauncher>();

        // Core declares IClipboard in TerminalClaudeRunner.cs and leaves it unimplemented: there is
        // no clipboard in a UI-free library.
        services.TryAddSingleton<IClipboard, AvaloniaClipboard>();

        services.TryAddSingleton<IPreflightFixApplier, PreflightFixApplier>();
        services.TryAddSingleton<IRunHistoryEditor, RunHistoryEditor>();
        services.TryAddSingleton<IAutoModeEnvironmentStore, ClaudeUserSettingsAutoModeEnvironmentStore>();

        services.TryAddSingleton<SingleInstanceGuard>();
        services.TryAddSingleton<StartWithWindowsRegistrar>();

        // Replaced by the view layer once a TopLevel exists; see the class remarks.
        services.TryAddSingleton<UnavailablePathPicker>();
        services.TryAddSingleton<IFolderPicker>(sp => sp.GetRequiredService<UnavailablePathPicker>());
        services.TryAddSingleton<IFilePicker>(sp => sp.GetRequiredService<UnavailablePathPicker>());
        services.TryAddSingleton<IConfirmationService, DeclineConfirmationService>();
    }

    static void AddViewModels(IServiceCollection services)
    {
        services.TryAddSingleton<DashboardViewModel>();
        services.TryAddSingleton<HistoryViewModel>();
        services.TryAddSingleton<SettingsViewModel>();
        services.TryAddSingleton<MainWindowViewModel>();
    }
}
