using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using NightShift.Desktop.ViewModels;
using NightShift.Desktop.Views;
using Serilog;

namespace NightShift.Desktop.Platform;

/// <summary>
/// The tray icon (plan.md §9.4): tooltip bound to <see cref="MainWindowViewModel.TrayTooltip"/>, an
/// icon whose colour reflects idle / running / blocked / paused, and a
/// <c>Show</c> / <c>Run now</c> / <c>Pause</c> / <c>Quit</c> menu.
/// </summary>
/// <remarks>
/// <para>
/// Built in code rather than declared in <c>App.axaml</c>. A declared <c>TrayIcon</c> binds against
/// the <see cref="Application"/>'s data context, which would mean giving the application object a
/// view model purely so a tooltip could find it; here the view model is simply passed in.
/// </para>
/// <para>
/// The four icons are drawn at startup rather than shipped as assets, so the state colours are the
/// same literals the <c>UsageGauge</c> uses and a change to one cannot silently diverge from the
/// other.
/// </para>
/// </remarks>
public sealed class NightShiftTrayIcon : IDisposable
{
    readonly MainWindowViewModel _viewModel;
    readonly TrayIcon _icon;
    readonly NativeMenuItem _pauseItem;
    readonly NativeMenuItem _runNowItem;
    readonly Dictionary<PilotStatusKind, WindowIcon> _icons;
    bool _disposed;

    NightShiftTrayIcon(
        MainWindowViewModel viewModel,
        Dictionary<PilotStatusKind, WindowIcon> icons,
        Action showWindow,
        Action quit)
    {
        _viewModel = viewModel;
        _icons = icons;

        _runNowItem = new NativeMenuItem("Run now")
        {
            Command = viewModel.Dashboard.RunNowCommand,
        };

        _pauseItem = new NativeMenuItem("Pause")
        {
            Command = new RelayCommand(TogglePause),
        };

        var menu = new NativeMenu
        {
            Items =
            {
                new NativeMenuItem("Show NightShift") { Command = new RelayCommand(showWindow) },
                _runNowItem,
                _pauseItem,
                new NativeMenuItemSeparator(),
                new NativeMenuItem("Quit") { Command = new RelayCommand(quit) },
            },
        };

        _icon = new TrayIcon
        {
            Menu = menu,
            ToolTipText = viewModel.TrayTooltip,
            // Left-clicking the icon is the same as the first menu entry; that is what every tray
            // app does, and a user who has hidden the window should not have to find a menu.
            Command = new RelayCommand(showWindow),
            IsVisible = true,
        };

        Refresh();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>
    /// Creates the tray icon, or returns null when the platform has no tray (or refuses one). The
    /// caller must treat null as "there is no tray", because hiding the window on close would
    /// otherwise leave the app unreachable.
    /// </summary>
    public static NightShiftTrayIcon? TryCreate(MainWindowViewModel viewModel, Action showWindow, Action quit)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(showWindow);
        ArgumentNullException.ThrowIfNull(quit);

        try
        {
            return new NightShiftTrayIcon(viewModel, BuildIcons(), showWindow, quit);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The tray icon could not be created; NightShift will run without one.");
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        // Without this the icon survives the process on Windows until something hovers over it.
        _icon.IsVisible = false;
        _icon.Dispose();
    }

    void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MainWindowViewModel.TrayTooltip)
            or nameof(MainWindowViewModel.StatusKind)
            or nameof(MainWindowViewModel.IsPaused)))
        {
            return;
        }

        // The view models raise property changes from background threads (a status refresh that
        // follows an awaited usage lookup, for one), and TrayIcon is an AvaloniaObject: writing to
        // it off the UI thread throws.
        if (Dispatcher.UIThread.CheckAccess())
        {
            Refresh();
        }
        else
        {
            Dispatcher.UIThread.Post(Refresh);
        }
    }

    void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        _icon.ToolTipText = _viewModel.TrayTooltip;

        if (_icons.TryGetValue(_viewModel.StatusKind, out var icon))
        {
            _icon.Icon = icon;
        }

        _pauseItem.Header = _viewModel.IsPaused ? "Resume" : "Pause";
    }

    void TogglePause()
    {
        var dashboard = _viewModel.Dashboard;

        if (_viewModel.IsPaused)
        {
            if (dashboard.ResumeCommand.CanExecute(null))
            {
                dashboard.ResumeCommand.Execute(null);
            }
        }
        else if (dashboard.PauseCommand.CanExecute(null))
        {
            dashboard.PauseCommand.Execute(null);
        }

        _viewModel.RefreshStatus();
        Refresh();
    }

    /// <summary>
    /// One icon per status. The same green / amber / red vocabulary as the gauges, so "red in the
    /// tray" and "red on the dashboard" mean the same thing without the window being open.
    /// </summary>
    static Dictionary<PilotStatusKind, WindowIcon> BuildIcons() => new()
    {
        [PilotStatusKind.Idle] = DrawIcon(Color.Parse("#60A5FA")),
        [PilotStatusKind.Running] = DrawIcon(Color.Parse("#4ADE80")),
        [PilotStatusKind.Paused] = DrawIcon(Color.Parse("#A1A1AA")),
        [PilotStatusKind.Blocked] = DrawIcon(Color.Parse("#F87171")),
        [PilotStatusKind.RateLimited] = DrawIcon(Color.Parse("#FBBF24")),
    };

    /// <summary>
    /// A crescent on a dark disc, in the status colour. Drawn rather than shipped so the palette
    /// lives in exactly one place.
    /// </summary>
    static WindowIcon DrawIcon(Color color)
    {
        const int side = 32;

        var bitmap = new RenderTargetBitmap(new PixelSize(side, side), new Vector(96, 96));

        using (var context = bitmap.CreateDrawingContext())
        {
            var centre = new Point(side / 2d, side / 2d);
            var background = new SolidColorBrush(Color.FromArgb(255, 24, 24, 27));
            var foreground = new SolidColorBrush(color);

            context.DrawEllipse(background, null, centre, 15.5, 15.5);

            // Crescent: a filled disc with a smaller one punched out of it by over-painting in the
            // background colour. Cheap, and unambiguous at 16 device pixels.
            context.DrawEllipse(foreground, null, centre, 10, 10);
            context.DrawEllipse(background, null, new Point(centre.X + 5.5, centre.Y - 3.5), 9, 9);
        }

        return new WindowIcon(bitmap);
    }
}
