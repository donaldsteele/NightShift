using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NightShift.Core.Configuration;
using NightShift.Core.Execution;
using NightShift.Core.History;
using NightShift.Core.Preflight;
using NightShift.Core.Scheduling;
using NightShift.Core.Usage;
using NightShift.Desktop.Services;
using NightShift.Desktop.ViewModels;

namespace NightShift.Desktop.Tests;

/// <summary>
/// Builds a real <see cref="PilotScheduler"/> over fake collaborators, plus the view models that
/// watch it, all rooted in a temp directory.
/// </summary>
/// <remarks>
/// <para>
/// The scheduler is real rather than substituted for one reason: <c>CycleCompleted</c> and
/// <c>RunProgress</c> are field-like events, so nothing outside the class can raise them. The only
/// honest way to test what the view models do with a completed cycle is to make a cycle complete —
/// <see cref="PilotScheduler.RunNowAsync"/> does that synchronously, without the background loop.
/// </para>
/// <para>
/// Everything below the scheduler is fake: no process is ever spawned, no network call is made, and
/// the only files touched are under <see cref="Root"/>.
/// </para>
/// </remarks>
sealed class ViewModelHarness : IDisposable
{
    public ViewModelHarness(PilotSettings? settings = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "nightshift-vm-tests", Guid.NewGuid().ToString("n"));
        Paths = new AppPaths(Root);
        Paths.EnsureCreated();

        Time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 26, 2, 0, 0, TimeSpan.Zero));
        Settings = new FakeSettingsStore(settings);
        UsageProvider = new FakeUsageProvider();
        Preflight = new FakePreflightChecker();
        FixApplier = new FakePreflightFixApplier();
        Runner = new FakeClaudeRunner();
        Shell = new FakeShellLauncher();
        Clipboard = new FakeClipboard();
        Confirmation = new FakeConfirmationService();
        Picker = new FakePathPicker();
        HistoryEditor = new FakeRunHistoryEditor();
        Dispatcher = ImmediateUiDispatcher.Instance;

        Usage = new CompositeUsageProvider(
            [UsageProvider],
            Settings,
            NullLogger<CompositeUsageProvider>.Instance,
            Time);

        History = new RunHistoryStore(Paths, NullLogger<RunHistoryStore>.Instance);
        StateStore = new PilotStateStore(Paths, NullLogger<PilotStateStore>.Instance);

        Scheduler = new PilotScheduler(
            Settings,
            Usage,
            Preflight,
            new RunGate(),
            StateStore,
            History,
            [Runner],
            NullLogger<PilotScheduler>.Instance,
            Time);
    }

    public string Root { get; }

    public AppPaths Paths { get; }

    public FakeTimeProvider Time { get; }

    public FakeSettingsStore Settings { get; }

    public FakeUsageProvider UsageProvider { get; }

    public CompositeUsageProvider Usage { get; }

    public FakePreflightChecker Preflight { get; }

    public FakePreflightFixApplier FixApplier { get; }

    public FakeClaudeRunner Runner { get; }

    public FakeShellLauncher Shell { get; }

    public FakeClipboard Clipboard { get; }

    public FakeConfirmationService Confirmation { get; }

    public FakePathPicker Picker { get; }

    public FakeRunHistoryEditor HistoryEditor { get; }

    public IUiDispatcher Dispatcher { get; }

    public RunHistoryStore History { get; }

    public PilotStateStore StateStore { get; }

    public PilotScheduler Scheduler { get; }

    public DashboardViewModel CreateDashboard(int outputCapacityLines = OutputRingBuffer.DefaultCapacity) =>
        new(
            Scheduler,
            Settings,
            Usage,
            Preflight,
            FixApplier,
            History,
            Paths,
            Dispatcher,
            Shell,
            Clipboard,
            Confirmation,
            Picker,
            Picker,
            NullLogger<DashboardViewModel>.Instance,
            Time,
            outputCapacityLines);

    public HistoryViewModel CreateHistory() =>
        new(
            History,
            HistoryEditor,
            Scheduler,
            Dispatcher,
            Shell,
            Clipboard,
            NullLogger<HistoryViewModel>.Instance);

    public SettingsViewModel CreateSettings(
        TimeSpan? debounce = null,
        FakeAutoModeEnvironmentStore? autoModeEnvironment = null) =>
        new(
            Settings,
            Dispatcher,
            Picker,
            Picker,
            Shell,
            new FakeExecutableLocator(),
            new AutoModeProbe(new FakeAutoModeProbeRunner(), NullLogger<AutoModeProbe>.Instance, "claude", Time),
            new PermissionsAskScanner(
                NullLogger<PermissionsAskScanner>.Instance,
                Path.Combine(Root, "user-settings.json")),
            new WorkspaceTrustManager(
                NullLogger<WorkspaceTrustManager>.Instance,
                Path.Combine(Root, ".claude.json")),
            new StartWithWindowsRegistrar(
                NullLogger<StartWithWindowsRegistrar>.Instance,
                "NightShift.Tests." + Guid.NewGuid().ToString("n")[..8],
                () => null),
            autoModeEnvironment ?? new FakeAutoModeEnvironmentStore(),
            Paths,
            NullLogger<SettingsViewModel>.Instance,
            Time,
            debounce ?? TimeSpan.FromMilliseconds(500));

    public MainWindowViewModel CreateMainWindow(
        DashboardViewModel? dashboard = null,
        HistoryViewModel? history = null,
        SettingsViewModel? settings = null) =>
        new(
            dashboard ?? CreateDashboard(),
            history ?? CreateHistory(),
            settings ?? CreateSettings(),
            Scheduler,
            Dispatcher,
            NullLogger<MainWindowViewModel>.Instance,
            Time);

    /// <summary>An all-green preflight, so a cycle gets as far as the usage gate.</summary>
    public void PreflightPasses() =>
        Preflight.Result = new PreflightResult(
            [new PreflightCheck(PreflightCheckId.PlanItems, "Plan has work left", PreflightStatus.Ok, "3 items left.")],
            Time.GetUtcNow(),
            new PlanItemCounts(12, 18, 0));

    /// <summary>A red preflight, so a cycle is blocked before it reaches usage.</summary>
    public void PreflightBlocks(PreflightFixAction? fix = null) =>
        Preflight.Result = new PreflightResult(
            [
                new PreflightCheck(
                    PreflightCheckId.ProjectDirectory,
                    "Project directory",
                    PreflightStatus.Error,
                    "No project directory has been chosen yet.")
                {
                    Fix = fix ?? new PreflightFixAction(
                        PreflightFixCommand.SetProjectDirectory, "Pick a directory…"),
                },
            ],
            Time.GetUtcNow());

    /// <summary>A snapshot where both windows report <paramref name="percent"/>.</summary>
    public UsageSnapshot Snapshot(
        double percent,
        DateTimeOffset? resetsAt = null,
        bool approximate = false,
        UsageSource source = UsageSource.OAuth)
    {
        var window = new UsageWindow(percent, resetsAt);
        return new UsageSnapshot(window, window, null, null, source, approximate, Time.GetUtcNow());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp directory that will not delete is not a test failure.
        }
    }
}
