using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NightShift.Core.History;
using NightShift.Core.Scheduling;
using NightShift.Desktop.Services;

namespace NightShift.Desktop.ViewModels;

/// <summary>What the status pill is saying (plan.md §9.1, §6.1).</summary>
public enum PilotStatusKind
{
    /// <summary>Enabled, nothing in flight, waiting for the next check.</summary>
    Idle,

    /// <summary>A run holds the gate.</summary>
    Running,

    /// <summary>The user paused the pilot; cycles still tick but every one skips.</summary>
    Paused,

    /// <summary>The last cycle refused to start — over threshold, no usage figure, or preflight.</summary>
    Blocked,

    /// <summary>
    /// A run started healthy and hit the quota wall partway through (plan.md §6.1). Distinct from
    /// <see cref="Blocked"/>: nothing is wrong, work is half-finished, and the pilot is waiting for
    /// a known moment to resume the same session.
    /// </summary>
    RateLimited,
}

/// <summary>
/// The shell: left-rail navigation between the three sections, the window title, and the status
/// pill (plan.md §9, §9.1).
/// </summary>
/// <remarks>
/// <para>
/// This view model owns the app's single one-second clock. Every countdown in the UI — the status
/// pill, the gauge sub-captions, the elapsed-run text — is re-rendered from
/// <see cref="Tick"/>, so there is exactly one timer rather than one per screen.
/// </para>
/// <para>
/// <b>Wiring contract for the view.</b> Construct through DI, set as the window's
/// <c>DataContext</c>, then <c>await InitializeAsync()</c> once (it starts the clock and does the
/// first load), and <see cref="Dispose"/> on window close.
/// </para>
/// </remarks>
public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    /// <summary>Window title (plan.md §9: <c>MainWindow</c>).</summary>
    public const string ApplicationTitle = "NightShift";

    /// <summary>How often the countdowns are re-rendered.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    readonly PilotScheduler _scheduler;
    readonly IUiDispatcher _dispatcher;
    readonly ILogger<MainWindowViewModel> _logger;
    readonly TimeProvider _time;
    readonly EventHandler<CycleCompleted> _onCycleCompleted;

    ITimer? _timer;
    SkipReason _lastSkipReason;
    double? _lastSkipMetricPercent;
    bool _disposed;

    public MainWindowViewModel(
        DashboardViewModel dashboard,
        HistoryViewModel history,
        SettingsViewModel settings,
        PlanDocumentViewModel plan,
        PilotScheduler scheduler,
        IUiDispatcher dispatcher,
        ILogger<MainWindowViewModel> logger,
        TimeProvider? timeProvider = null)
    {
        Dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
        History = history ?? throw new ArgumentNullException(nameof(history));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = timeProvider ?? TimeProvider.System;

        Sections =
        [
            new NavigationItemViewModel(NavigationSection.Dashboard, "Dashboard", "Usage, preflight and the live run"),
            new NavigationItemViewModel(NavigationSection.History, "History", "Past runs and their transcripts"),
            new NavigationItemViewModel(NavigationSection.Settings, "Settings", "Project, schedule, autonomy and prompt"),
        ];

        SelectedNavigationItem = Sections[0];

        _onCycleCompleted = (_, cycle) => _dispatcher.Post(() => OnCycleCompleted(cycle));
        _scheduler.CycleCompleted += _onCycleCompleted;

        RefreshStatus();
    }

    /// <summary>
    /// The plan window's view model. Held here rather than only in the presenter so an unsaved
    /// edit is flushed on the same shutdown path that flushes a settings debounce.
    /// </summary>
    public PlanDocumentViewModel Plan { get; }

    /// <summary>plan.md §9.1.</summary>
    public DashboardViewModel Dashboard { get; }

    /// <summary>plan.md §9.3.</summary>
    public HistoryViewModel History { get; }

    /// <summary>plan.md §9.2.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>The three left-rail destinations, in rail order.</summary>
    public IReadOnlyList<NavigationItemViewModel> Sections { get; }

    /// <summary>
    /// The visible section. Kept in sync with <see cref="SelectedNavigationItem"/> both ways, so a
    /// rail <c>SelectedItem</c> binding and a programmatic <see cref="NavigateCommand"/> agree.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentViewModel))]
    public partial NavigationSection CurrentSection { get; set; } = NavigationSection.Dashboard;

    /// <summary>Two-way binding target for the rail's <c>SelectedItem</c>.</summary>
    [ObservableProperty]
    public partial NavigationItemViewModel SelectedNavigationItem { get; set; }

    /// <summary>The view model for <see cref="CurrentSection"/>, for a <c>ContentControl</c>.</summary>
    public ViewModelBase CurrentViewModel => CurrentSection switch
    {
        NavigationSection.History => History,
        NavigationSection.Settings => Settings,
        _ => Dashboard,
    };

    /// <summary>Window title: <c>NightShift</c>, or <c>NightShift — Dashboard</c> style if wanted.</summary>
    [ObservableProperty]
    public partial string Title { get; private set; } = ApplicationTitle;

    // ── Status pill (plan.md §9.1) ─────────────────────────────────────────────────────────────

    /// <summary>The pill's state, for styling. Text is <see cref="StatusText"/>.</summary>
    [ObservableProperty]
    public partial PilotStatusKind StatusKind { get; private set; } = PilotStatusKind.Idle;

    /// <summary>
    /// The pill text, verbatim per plan.md §9.1: <c>Idle · next run in 42m</c>,
    /// <c>Running — 3m elapsed</c>, <c>Paused</c>, <c>Blocked: usage 94%</c>, plus the §6.1
    /// mid-run quota state, <c>Rate limited · quota returns in 2h 14m</c>.
    /// </summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; } = "Idle";

    /// <summary>A longer sentence for a tooltip: the last cycle's own explanation.</summary>
    [ObservableProperty]
    public partial string StatusDetail { get; private set; } = string.Empty;

    /// <summary>Tray tooltip (plan.md §9.4): <c>NightShift — next run in 42m</c>.</summary>
    [ObservableProperty]
    public partial string TrayTooltip { get; private set; } = ApplicationTitle;

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    public partial bool IsPaused { get; private set; }

    /// <summary>When the next scheduled check is due. Null before the scheduler has scheduled one.</summary>
    [ObservableProperty]
    public partial DateTimeOffset? NextRunAt { get; private set; }

    // ── Lifecycle ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// First load for every section, then the clock starts. Call exactly once, after construction.
    /// Never throws: a section that fails to load leaves the rest of the window usable.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await SafeAsync(() => Settings.InitializeAsync(cancellationToken), nameof(Settings)).ConfigureAwait(false);
        await SafeAsync(() => Dashboard.InitializeAsync(cancellationToken), nameof(Dashboard)).ConfigureAwait(false);
        await SafeAsync(() => History.InitializeAsync(cancellationToken), nameof(History)).ConfigureAwait(false);

        RefreshStatus();
        StartClock();
    }

    /// <summary>
    /// Starts the one-second clock. Called by <see cref="InitializeAsync"/>; separate so a test can
    /// drive <see cref="Tick"/> by hand and never start a real timer.
    /// </summary>
    public void StartClock()
    {
        _timer ??= _time.CreateTimer(
            _ => _dispatcher.Post(Tick),
            null,
            TickInterval,
            TickInterval);
    }

    /// <summary>Re-renders everything that depends on the current time.</summary>
    public void Tick()
    {
        if (_disposed)
        {
            return;
        }

        Dashboard.Tick();
        RefreshStatus();
    }

    /// <summary>Recomputes the status pill from the scheduler, the dashboard and the last cycle.</summary>
    public void RefreshStatus()
    {
        var now = _time.GetUtcNow();

        // Deliberately the dashboard's flag rather than the scheduler's gate. The gate is still held
        // when CycleCompleted is raised, so reading it here would leave the pill saying "Running"
        // for a cycle that has finished, until the next tick corrected it. The dashboard is
        // constructed before this view model and therefore subscribes first, so by the time this
        // runs its flag is already up to date.
        IsRunning = Dashboard.IsRunning;
        IsPaused = !_scheduler.IsEnabled;
        NextRunAt = _scheduler.NextRunAt;

        // Precedence is deliberate. "Running" beats everything because it is the only state that is
        // happening rather than being waited on. "Paused" beats the two waiting states because the
        // user asked for it, and telling them the quota is the reason nothing is happening would be
        // a lie. "Rate limited" beats "Blocked" because it is the more specific diagnosis of the
        // same silence, and it carries a known return time.
        if (IsRunning)
        {
            StatusKind = PilotStatusKind.Running;
            var elapsed = Dashboard.RunStartedAt is { } startedAt
                ? TimeSpanText.DescribeElapsed(now - startedAt)
                : "0s";
            StatusText = $"Running — {elapsed} elapsed";
        }
        else if (IsPaused)
        {
            StatusKind = PilotStatusKind.Paused;
            StatusText = "Paused";
        }
        else if (Dashboard.IsRateLimited)
        {
            StatusKind = PilotStatusKind.RateLimited;
            StatusText = Dashboard.RateLimitResetsAt is { } resetsAt && resetsAt > now
                ? $"Rate limited · quota returns {TimeSpanText.DescribeUntil(resetsAt, now)}"
                : "Rate limited · waiting for quota";
        }
        else if (DescribeBlock() is { } blocked)
        {
            StatusKind = PilotStatusKind.Blocked;
            StatusText = blocked;
        }
        else
        {
            StatusKind = PilotStatusKind.Idle;
            StatusText = NextRunAt is { } next && next > now
                ? $"Idle · next run {TimeSpanText.DescribeUntil(next, now)}"
                : "Idle";
        }

        StatusDetail = Dashboard.StatusMessage;
        TrayTooltip = NextRunAt is { } due && due > now && StatusKind == PilotStatusKind.Idle
            ? $"{ApplicationTitle} — next run {TimeSpanText.DescribeUntil(due, now)}"
            : $"{ApplicationTitle} — {StatusText}";
    }

    /// <summary>
    /// The orderly close: writes any settings edit that is still inside its debounce window, then
    /// disposes. Call this from the window's closing handler rather than <see cref="Dispose"/>
    /// alone — a debounce still counting when the process exits loses the user's last change.
    /// </summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await Settings.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not flush pending settings on shutdown.");
        }

        try
        {
            // Asks rather than saves. Settings are small and reversible; a plan is a document a
            // background run is also writing to, and silently saving over what that run just
            // committed is the one outcome nobody could undo.
            await Plan.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not flush an unsaved plan edit on shutdown.");
        }

        Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _scheduler.CycleCompleted -= _onCycleCompleted;
        _timer?.Dispose();
        _timer = null;

        Dashboard.Dispose();
        History.Dispose();
        Settings.Dispose();
        Plan.Dispose();
    }

    // ── Navigation ─────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    void Navigate(NavigationSection section) => CurrentSection = section;

    partial void OnCurrentSectionChanged(NavigationSection value)
    {
        var item = Sections.FirstOrDefault(section => section.Section == value);
        if (item is not null && !ReferenceEquals(SelectedNavigationItem, item))
        {
            SelectedNavigationItem = item;
        }
    }

    partial void OnSelectedNavigationItemChanged(NavigationItemViewModel value)
    {
        if (value is not null)
        {
            CurrentSection = value.Section;
        }
    }

    // ── Internals ──────────────────────────────────────────────────────────────────────────────

    void OnCycleCompleted(CycleCompleted cycle)
    {
        _lastSkipReason = cycle.Decision.ShouldRun ? SkipReason.None : cycle.Decision.Reason;
        _lastSkipMetricPercent = cycle.Decision.MetricPercent;
        RefreshStatus();
    }

    /// <summary>
    /// The <c>Blocked: …</c> wording, or null when the last cycle was not a blocking skip.
    /// <see cref="SkipReason.AlreadyRunning"/> and <see cref="SkipReason.Disabled"/> deliberately do
    /// not block: they are covered by the Running and Paused states above.
    /// </summary>
    string? DescribeBlock() => _lastSkipReason switch
    {
        SkipReason.OverThreshold => _lastSkipMetricPercent is { } percent
            ? "Blocked: usage " + percent.ToString("0", CultureInfo.InvariantCulture) + "%"
            : "Blocked: usage over threshold",
        SkipReason.UsageUnavailable => "Blocked: usage unavailable",
        SkipReason.PreflightFailed => "Blocked: preflight",
        SkipReason.DryRun => "Blocked: dry run",
        _ => null,
    };

    async Task SafeAsync(Func<Task> action, string what)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "{Section} failed to initialize; the rest of the window still loads.", what);
        }
    }
}
