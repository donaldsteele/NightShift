using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using NightShift.Desktop.Platform;
using NightShift.Desktop.Services;
using NightShift.Desktop.ViewModels;
using NightShift.Desktop.Views;
using Serilog;

namespace NightShift.Desktop;

public partial class App : Application
{
    /// <summary>How long <see cref="MainWindowViewModel.ShutdownAsync"/> gets to flush before exit.</summary>
    static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    readonly CancellationTokenSource _lifetime = new();

    MainWindowViewModel? _viewModel;
    MainWindow? _window;
    NightShiftTrayIcon? _tray;
    bool _exiting;

    /// <summary>
    /// Service provider from the generic host. Set by <see cref="Program"/> before Avalonia starts;
    /// null in the XAML designer, which constructs <see cref="App"/> without a host.
    /// </summary>
    public static IServiceProvider? Services { get; set; }

    /// <summary>
    /// plan.md §9.2 Schedule: start minimized to tray. Read from <c>PilotSettings</c> by
    /// <see cref="Program"/> once the host has loaded settings, because the window is created before
    /// any view model has been asked anything.
    /// </summary>
    public static bool StartMinimizedToTray { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// The platform services the view models declare but neither Core nor the service layer can
    /// supply, because each one needs a <see cref="TopLevel"/>.
    /// </summary>
    /// <remarks>
    /// <b>Call this before <c>AddNightShiftDesktop()</c>.</b> The placeholders in
    /// <see cref="DesktopServiceCollectionExtensions"/> are registered with <c>TryAdd</c>, which
    /// keeps whatever is already there — so the first registration wins, and registering afterwards
    /// would silently leave the fail-closed stubs in place.
    /// </remarks>
    public static void RegisterPlatformServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<StorageProviderPathPicker>();
        services.AddSingleton<IFolderPicker>(sp => sp.GetRequiredService<StorageProviderPathPicker>());
        services.AddSingleton<IFilePicker>(sp => sp.GetRequiredService<StorageProviderPathPicker>());
        services.AddSingleton<IConfirmationService, DialogConfirmationService>();
    }

    /// <summary>
    /// Brings the window back from the tray. Safe from any thread: a second launch signals this from
    /// its own process and the listener runs on a background thread (plan.md §9.4).
    /// </summary>
    public static void SurfaceExistingWindow()
    {
        try
        {
            Dispatcher.UIThread.Post(() => (Current as App)?.ShowMainWindow());
        }
        catch (InvalidOperationException ex)
        {
            // Avalonia is not up yet; the window is about to show itself anyway.
            Log.Debug(ex, "Could not surface the main window: the dispatcher is not running.");
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Closing the window can mean "hide to tray" (plan.md §9.4), so the window is not the
            // app's lifetime. Quit is the only thing that ends the process.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _viewModel = Services?.GetService<MainWindowViewModel>();
            _window = new MainWindow { DataContext = _viewModel };
            _window.Opened += OnWindowOpened;
            _window.Closing += OnWindowClosing;

            desktop.MainWindow = _window;

            if (_viewModel is not null)
            {
                _tray = NightShiftTrayIcon.TryCreate(_viewModel, ShowMainWindow, Quit);
                _ = InitializeViewModelAsync(_viewModel);
            }
            else
            {
                Log.Error("No service provider: the window will render without a view model.");
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Shows, un-minimizes and focuses the window.</summary>
    public void ShowMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    /// <summary>plan.md §9.4: <c>Quit</c> genuinely exits, rather than hiding to the tray again.</summary>
    public void Quit() => _ = ShutdownAndExitAsync();

    /// <summary>
    /// The one and only <see cref="MainWindowViewModel.InitializeAsync"/> call. Fire-and-forget
    /// deliberately: the window is already on screen and each section fills itself in as it loads,
    /// which beats a frozen splash while usage is fetched over the network.
    /// </summary>
    async Task InitializeViewModelAsync(MainWindowViewModel viewModel)
    {
        try
        {
            await viewModel.InitializeAsync(_lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // The app is closing mid-load.
        }
        catch (Exception ex)
        {
            Log.Error(ex, "The main window view model failed to initialize.");
        }
    }

    void OnWindowOpened(object? sender, EventArgs e)
    {
        if (_window is null)
        {
            return;
        }

        _window.Opened -= OnWindowOpened;

        // Only hide when there is somewhere to hide to. Without a tray icon this would leave a
        // running scheduler and no way to reach it.
        if (StartMinimizedToTray && _tray is not null)
        {
            _window.Hide();
        }
    }

    void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (_exiting)
        {
            return;
        }

        // Cancelled either way: hiding is not closing, and a real exit has to flush settings first,
        // which cannot be awaited inside this handler.
        e.Cancel = true;

        if (_tray is not null && _viewModel?.Settings.StartMinimized == true)
        {
            _window?.Hide();
            return;
        }

        _ = ShutdownAndExitAsync();
    }

    /// <summary>
    /// The orderly exit: flush a settings edit still inside its debounce, detach the view models
    /// from the scheduler, drop the tray icon, then end the process.
    /// </summary>
    async Task ShutdownAndExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;

        if (_viewModel is not null)
        {
            using var timeout = new CancellationTokenSource(ShutdownTimeout);
            try
            {
                // ShutdownAsync, not Dispose: a settings edit made in the last second of the session
                // is still sitting in its debounce window, and Dispose alone would drop it.
                await _viewModel.ShutdownAsync(timeout.Token).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Shutting the view models down failed; exiting anyway.");
            }
        }

        await _lifetime.CancelAsync().ConfigureAwait(true);

        _tray?.Dispose();
        _tray = null;

        if (_window is not null)
        {
            _window.Closing -= OnWindowClosing;

            // Detach before closing, so no binding outlives the window it was drawing into.
            _window.DataContext = null;
            _window.Close();
            _window = null;
        }

        _viewModel = null;
        _lifetime.Dispose();

        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
