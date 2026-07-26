using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using NightShift.Desktop.ViewModels;
using NightShift.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;

namespace NightShift.Desktop;

public partial class App : Application
{
    /// <summary>
    /// Service provider from the generic host. Set by <see cref="Program"/> before Avalonia starts;
    /// null in the XAML designer, which constructs <see cref="App"/> without a host.
    /// </summary>
    public static IServiceProvider? Services { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = Services?.GetService<MainViewModel>() ?? new MainViewModel(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
