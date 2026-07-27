using Microsoft.Extensions.DependencyInjection;
using NightShift.Core;
using NightShift.Core.Configuration;
using NightShift.Core.Execution;
using NightShift.Desktop.Services;
using NightShift.Desktop.ViewModels;

namespace NightShift.Desktop.Tests;

/// <summary>
/// <c>AddNightShiftDesktop</c> is the contract between this work and the app host, so it gets a
/// test: the whole graph must resolve, and the singletons must really be singletons.
/// </summary>
public class CompositionTests
{
    static ServiceProvider Build(string root)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNightShiftCore(new AppPaths(root));
        services.AddNightShiftDesktop();
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });
    }

    [Fact]
    public void TheWholeViewModelGraphResolves()
    {
        var root = Path.Combine(Path.GetTempPath(), "nightshift-di", Guid.NewGuid().ToString("n"));
        using var provider = Build(root);

        var main = provider.GetRequiredService<MainWindowViewModel>();

        Assert.NotNull(main.Dashboard);
        Assert.NotNull(main.History);
        Assert.NotNull(main.Settings);
        Assert.NotNull(main.Plan);

        // The plan window is not a rail section: it is its own window, opened from the card.
        Assert.Equal(3, main.Sections.Count);
    }

    [Fact]
    public void TheDesktopSuppliesTheClipboardCoreDeclaresButCannotImplement()
    {
        var root = Path.Combine(Path.GetTempPath(), "nightshift-di", Guid.NewGuid().ToString("n"));
        using var provider = Build(root);

        Assert.IsType<AvaloniaClipboard>(provider.GetRequiredService<IClipboard>());
    }

    [Fact]
    public void ViewModelsAreSingletonsSoEventSubscriptionsCannotAccumulate()
    {
        var root = Path.Combine(Path.GetTempPath(), "nightshift-di", Guid.NewGuid().ToString("n"));
        using var provider = Build(root);

        Assert.Same(
            provider.GetRequiredService<DashboardViewModel>(),
            provider.GetRequiredService<MainWindowViewModel>().Dashboard);
        Assert.Same(
            provider.GetRequiredService<SettingsViewModel>(),
            provider.GetRequiredService<SettingsViewModel>());

        // One plan view model, so the dashboard refreshes off the same instance the window shows.
        Assert.Same(
            provider.GetRequiredService<PlanDocumentViewModel>(),
            provider.GetRequiredService<MainWindowViewModel>().Plan);
    }

    [Fact]
    public void ThePlaceholderPickersAndConfirmationFailClosed()
    {
        var root = Path.Combine(Path.GetTempPath(), "nightshift-di", Guid.NewGuid().ToString("n"));
        using var provider = Build(root);

        Assert.IsType<UnavailablePathPicker>(provider.GetRequiredService<IFolderPicker>());
        Assert.IsType<UnavailablePathPicker>(provider.GetRequiredService<IFilePicker>());
        Assert.IsType<DeclineConfirmationService>(provider.GetRequiredService<IConfirmationService>());

        // The plan window's placeholder fails quiet rather than closed: nothing unsafe follows from
        // a window that did not open, but a silent no-op with no log line reads as a broken button.
        Assert.IsType<UnavailablePlanWindowPresenter>(provider.GetRequiredService<IPlanWindowPresenter>());
    }

    [Fact]
    public void TheViewLayerCanReplaceThePlaceholdersBeforeCallingAdd()
    {
        var root = Path.Combine(Path.GetTempPath(), "nightshift-di", Guid.NewGuid().ToString("n"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNightShiftCore(new AppPaths(root));

        // TryAdd means a registration made first wins, which is how the view supplies the real
        // IStorageProvider-backed pickers once a TopLevel exists.
        services.AddSingleton<IFolderPicker, FakePathPicker>();
        services.AddNightShiftDesktop();

        using var provider = services.BuildServiceProvider();
        Assert.IsType<FakePathPicker>(provider.GetRequiredService<IFolderPicker>());
    }
}

public class MainWindowShutdownTests
{
    [Fact]
    public async Task ShutdownWritesAnEditThatWasStillInsideItsDebounce()
    {
        using var harness = new ViewModelHarness();
        var settings = harness.CreateSettings(TimeSpan.FromMinutes(5));
        var main = harness.CreateMainWindow(settings: settings);

        settings.PlanFileName = "roadmap.md";
        Assert.Equal(0, harness.Settings.SaveCount);

        await main.ShutdownAsync();

        Assert.Equal(1, harness.Settings.SaveCount);
        Assert.Equal("roadmap.md", harness.Settings.Current.PlanFileName);
    }

    [Fact]
    public async Task ShutdownIsIdempotent()
    {
        using var harness = new ViewModelHarness();
        var main = harness.CreateMainWindow();

        await main.ShutdownAsync();
        await main.ShutdownAsync();
        main.Dispose();
    }
}
