using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NightShift.Core.Configuration;
using NightShift.Core.Execution;
using NightShift.Core.Preflight;
using NightShift.Core.Usage;
using NightShift.Desktop.Services;

namespace NightShift.Desktop.ViewModels;

/// <summary>
/// Every option in plan.md §9.2, grouped as the plan describes, saved on change with a debounce and
/// no OK/Cancel.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no Save button on purpose.</b> Any edit schedules a write
/// <see cref="DebounceInterval"/> later; a further edit within that window restarts the timer, so
/// dragging the threshold slider writes once rather than fifty times. <see cref="FlushAsync"/>
/// forces the pending write out — call it when the window closes, and in tests instead of sleeping.
/// </para>
/// <para>
/// Two controls here do <b>not</b> live in <see cref="PilotSettings"/>, and both are called out in
/// the plan: "Start with Windows" is a registry value (§9.4) and the <c>autoMode.environment</c>
/// editor writes Claude Code's own <c>~/.claude/settings.json</c> (§5.3.2).
/// </para>
/// </remarks>
public sealed partial class SettingsViewModel : ViewModelBase, IDisposable
{
    /// <summary>Default quiet period before an edit is written.</summary>
    public static readonly TimeSpan DefaultDebounceInterval = TimeSpan.FromMilliseconds(600);

    /// <summary>The warning printed beside any detected <c>permissions.ask</c> rule (plan.md §9.2).</summary>
    public const string PermissionsAskNote =
        "These will hang a run. An ask rule is evaluated before the auto-mode classifier and always " +
        "forces a prompt, so an unattended run sits there until the stall detector kills it.";

    /// <summary>The warning printed beside the skip-permissions toggle (plan.md §5.3.1, §11.4).</summary>
    public const string SkipPermissionsWarning =
        "Disables all permission checking and the classifier with it. Isolated repositories only.";

    /// <summary>
    /// Properties whose change schedules a save. Kept explicit so adding a display-only property
    /// cannot start writing settings by accident, and so a test can assert the list is complete.
    /// </summary>
    public static readonly IReadOnlySet<string> PersistedProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(ProjectDirectory), nameof(PlanFileName),
        nameof(IntervalMinutes), nameof(RunOnStartup), nameof(StartWithWindows), nameof(StartMinimized),
        nameof(AlignToQuotaReset), nameof(QuotaResetGraceMinutes),
        nameof(ThresholdPercent), nameof(UsageMetric), nameof(OnUsageUnavailable),
        nameof(ClaudeExecutablePath), nameof(LaunchMode), nameof(Model), nameof(ResumeStrategy),
        nameof(MaxRunDurationMinutes), nameof(StallTimeoutMinutes),
        nameof(PermissionMode), nameof(AutoTrustProjectFolder), nameof(AllowedTools),
        nameof(SkipPermissionsFallback),
        nameof(CavemanLevel), nameof(PromptTemplate),
        nameof(UsageProviderOrder), nameof(CcusageCommandOverride), nameof(DryRun),
        nameof(HistoryRetentionCount),
    };

    readonly ISettingsStore _store;
    readonly IUiDispatcher _dispatcher;
    readonly IFolderPicker _folderPicker;
    readonly IFilePicker _filePicker;
    readonly IShellLauncher _shell;
    readonly IClaudeExecutableLocator _locator;
    readonly AutoModeProbe _autoModeProbe;
    readonly PermissionsAskScanner _permissionsScanner;
    readonly WorkspaceTrustManager _trustManager;
    readonly StartWithWindowsRegistrar _autorun;
    readonly IAutoModeEnvironmentStore _autoModeEnvironment;
    readonly AppPaths _paths;
    readonly ILogger<SettingsViewModel> _logger;
    readonly TimeProvider _time;
    readonly SemaphoreSlim _saveGate = new(1, 1);
    readonly EventHandler<PilotSettings> _onSettingsChanged;

    ITimer? _debounceTimer;
    int _suppressDepth;
    bool _disposed;

    public SettingsViewModel(
        ISettingsStore store,
        IUiDispatcher dispatcher,
        IFolderPicker folderPicker,
        IFilePicker filePicker,
        IShellLauncher shell,
        IClaudeExecutableLocator locator,
        AutoModeProbe autoModeProbe,
        PermissionsAskScanner permissionsScanner,
        WorkspaceTrustManager trustManager,
        StartWithWindowsRegistrar autorun,
        IAutoModeEnvironmentStore autoModeEnvironment,
        AppPaths paths,
        ILogger<SettingsViewModel> logger,
        TimeProvider? timeProvider = null,
        TimeSpan? debounceInterval = null)
    : base(dispatcher)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _folderPicker = folderPicker ?? throw new ArgumentNullException(nameof(folderPicker));
        _filePicker = filePicker ?? throw new ArgumentNullException(nameof(filePicker));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _autoModeProbe = autoModeProbe ?? throw new ArgumentNullException(nameof(autoModeProbe));
        _permissionsScanner = permissionsScanner ?? throw new ArgumentNullException(nameof(permissionsScanner));
        _trustManager = trustManager ?? throw new ArgumentNullException(nameof(trustManager));
        _autorun = autorun ?? throw new ArgumentNullException(nameof(autorun));
        _autoModeEnvironment = autoModeEnvironment ?? throw new ArgumentNullException(nameof(autoModeEnvironment));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = timeProvider ?? TimeProvider.System;
        DebounceInterval = debounceInterval ?? DefaultDebounceInterval;

        _onSettingsChanged = (_, updated) => _dispatcher.Post(() => ApplyExternalChange(updated));
        _store.SettingsChanged += _onSettingsChanged;

        Apply(_store.Current);
        StartWithWindows = _autorun.IsRegistered;
        RefreshPromptPreview();
    }

    /// <summary>Quiet period between the last edit and the write.</summary>
    public TimeSpan DebounceInterval { get; }

    /// <summary>True while an edit is waiting to be written.</summary>
    [ObservableProperty]
    public partial bool HasPendingChanges { get; private set; }

    /// <summary>When settings were last written. Null until the first save of this session.</summary>
    [ObservableProperty]
    public partial DateTimeOffset? LastSavedAt { get; private set; }

    /// <summary>
    /// A <c>settings.json</c> write failure, for an inline error. Empty when the last write
    /// succeeded. Deliberately narrow: it is cleared by the next successful save, so nothing else
    /// may borrow it to report an unrelated problem.
    /// </summary>
    [ObservableProperty]
    public partial string SaveError { get; private set; } = string.Empty;

    /// <summary>
    /// Why the "Start with Windows" toggle could not be applied. Its own property because the
    /// registry write has nothing to do with the settings file, and reusing
    /// <see cref="SaveError"/> would let the next settings save wipe the message.
    /// </summary>
    [ObservableProperty]
    public partial string StartWithWindowsError { get; private set; } = string.Empty;

    /// <summary>A transient one-line notice from an Advanced action, e.g. a failed folder open.</summary>
    [ObservableProperty]
    public partial string NoticeMessage { get; private set; } = string.Empty;

    // ── Project (plan.md §9.2) ─────────────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial string ProjectDirectory { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PlanFileName { get; set; } = "plan.md";

    // ── Schedule ───────────────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial int IntervalMinutes { get; set; } = 60;

    public static int IntervalMinimum => PilotSettings.MinIntervalMinutes;

    public static int IntervalMaximum => PilotSettings.MaxIntervalMinutes;

    [ObservableProperty]
    public partial bool RunOnStartup { get; set; }

    /// <summary>
    /// Not a <see cref="PilotSettings"/> value: this is
    /// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> (plan.md §9.4). Reverted
    /// automatically when the registry write fails, so the toggle can never claim something the
    /// machine does not do.
    /// </summary>
    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    /// <summary>False off Windows; the toggle should be disabled and explained.</summary>
    public bool IsStartWithWindowsSupported => _autorun.IsSupported;

    [ObservableProperty]
    public partial bool StartMinimized { get; set; }

    /// <summary>plan.md §6: anchor the next check to the soonest quota reset instead of ticking blind.</summary>
    [ObservableProperty]
    public partial bool AlignToQuotaReset { get; set; } = true;

    /// <summary>plan.md §6: how long after a reset to wake, so the wake does not race the server clock.</summary>
    [ObservableProperty]
    public partial int QuotaResetGraceMinutes { get; set; } = 1;

    public static int QuotaResetGraceMinimum => 0;

    public static int QuotaResetGraceMaximum => 60;

    // ── Usage gate ─────────────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial double ThresholdPercent { get; set; } = 90;

    public static double ThresholdMinimum => PilotSettings.MinThresholdPercent;

    public static double ThresholdMaximum => PilotSettings.MaxThresholdPercent;

    [ObservableProperty]
    public partial UsageMetric UsageMetric { get; set; } = UsageMetric.HighestOfAll;

    public IReadOnlyList<UsageMetric> UsageMetricOptions { get; } =
        [UsageMetric.HighestOfAll, UsageMetric.FiveHour, UsageMetric.SevenDay];

    [ObservableProperty]
    public partial UsageUnavailableBehavior OnUsageUnavailable { get; set; } = UsageUnavailableBehavior.Skip;

    public IReadOnlyList<UsageUnavailableBehavior> UsageUnavailableOptions { get; } =
        [UsageUnavailableBehavior.Skip, UsageUnavailableBehavior.Run];

    // ── Claude ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Explicit override. Empty means "auto-detect"; see <see cref="DetectedClaudePath"/>.</summary>
    [ObservableProperty]
    public partial string ClaudeExecutablePath { get; set; } = string.Empty;

    /// <summary>What auto-detection found, so the box can show a placeholder rather than nothing.</summary>
    [ObservableProperty]
    public partial string DetectedClaudePath { get; private set; } = string.Empty;

    /// <summary>Where auto-detection looked, when it found nothing.</summary>
    public ObservableCollection<string> ClaudeProbedPaths { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHeadless))]
    [NotifyPropertyChangedFor(nameof(IsVisibleTerminal))]
    public partial LaunchMode LaunchMode { get; set; } = LaunchMode.Headless;

    /// <summary>Radio-button binding for <see cref="Core.Configuration.LaunchMode.Headless"/>.</summary>
    public bool IsHeadless
    {
        get => LaunchMode == LaunchMode.Headless;
        set
        {
            if (value)
            {
                LaunchMode = LaunchMode.Headless;
            }
        }
    }

    /// <summary>Radio-button binding for <see cref="Core.Configuration.LaunchMode.VisibleTerminal"/>.</summary>
    public bool IsVisibleTerminal
    {
        get => LaunchMode == LaunchMode.VisibleTerminal;
        set
        {
            if (value)
            {
                LaunchMode = LaunchMode.VisibleTerminal;
            }
        }
    }

    /// <summary>Blank means "do not pass <c>--model</c>".</summary>
    [ObservableProperty]
    public partial string Model { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ResumeStrategy ResumeStrategy { get; set; } = ResumeStrategy.Fresh;

    public IReadOnlyList<ResumeStrategy> ResumeStrategyOptions { get; } =
        [ResumeStrategy.Fresh, ResumeStrategy.Resume];

    [ObservableProperty]
    public partial int MaxRunDurationMinutes { get; set; } = 55;

    [ObservableProperty]
    public partial int StallTimeoutMinutes { get; set; } = 10;

    // ── Autonomy ───────────────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial PermissionMode PermissionMode { get; set; } = PermissionMode.Auto;

    /// <summary><c>plan</c> is deliberately absent: it ends by waiting for a human.</summary>
    public IReadOnlyList<PermissionMode> PermissionModeOptions { get; } =
        [PermissionMode.Auto, PermissionMode.AcceptEdits, PermissionMode.BypassPermissions];

    /// <summary>
    /// The availability state shown inline next to the dropdown (plan.md §9.2 Autonomy), e.g.
    /// "Auto mode is available" or the reason a run would fall back to <c>acceptEdits</c>.
    /// </summary>
    [ObservableProperty]
    public partial string AutoModeAvailabilityText { get; private set; } = "Auto mode has not been probed yet.";

    [ObservableProperty]
    public partial AutoModeAvailability AutoModeAvailability { get; private set; } = AutoModeAvailability.Unknown;

    [ObservableProperty]
    public partial bool AutoTrustProjectFolder { get; set; } = true;

    /// <summary>
    /// The exact <c>~/.claude.json</c> keys the trust toggle will write, displayed underneath it
    /// (plan.md §9.2). Empty until a project directory is chosen.
    /// </summary>
    public ObservableCollection<string> TrustKeysPreview { get; } = [];

    /// <summary>The file those keys go into.</summary>
    public string TrustConfigPath => _trustManager.ConfigPath;

    [ObservableProperty]
    public partial string AllowedTools { get; set; } = PilotSettings.DefaultAllowedTools;

    [ObservableProperty]
    public partial bool SkipPermissionsFallback { get; set; }

    /// <summary>
    /// Read-only list of every <c>permissions.ask</c> rule and interactive MCP server found, with
    /// <see cref="PermissionsAskNote"/> beside it (plan.md §9.2 Autonomy, §5.3.2).
    /// </summary>
    public ObservableCollection<string> PermissionsAskRules { get; } = [];

    public bool HasPermissionsAskRules => PermissionsAskRules.Count > 0;

    /// <summary>Which settings files were scanned for those rules.</summary>
    public ObservableCollection<string> ScannedSettingsFiles { get; } = [];

    // ── Prompt ─────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial CavemanLevel CavemanLevel { get; set; } = CavemanLevel.Full;

    public IReadOnlyList<CavemanLevel> CavemanLevelOptions { get; } =
    [
        CavemanLevel.Lite, CavemanLevel.Full, CavemanLevel.Ultra,
        CavemanLevel.WenyanLite, CavemanLevel.WenyanFull, CavemanLevel.WenyanUltra,
    ];

    [ObservableProperty]
    public partial string PromptTemplate { get; set; } = PilotSettings.DefaultPromptTemplate;

    /// <summary>
    /// The exact string a run would be launched with, recomputed on every keystroke from
    /// <see cref="PromptBuilder"/> — the same pure function the runner uses, so the preview cannot
    /// drift from reality (plan.md §9.2 Prompt).
    /// </summary>
    [ObservableProperty]
    public partial string PromptPreview { get; private set; } = string.Empty;

    /// <summary>True when the template still matches <see cref="PilotSettings.DefaultPromptTemplate"/>.</summary>
    public bool IsPromptTemplateDefault =>
        string.Equals(PromptTemplate, PilotSettings.DefaultPromptTemplate, StringComparison.Ordinal);

    // ── Advanced ───────────────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial UsageProviderPreference UsageProviderOrder { get; set; } =
        UsageProviderPreference.OAuthThenCcusage;

    public IReadOnlyList<UsageProviderPreference> UsageProviderOrderOptions { get; } =
    [
        UsageProviderPreference.OAuthThenCcusage,
        UsageProviderPreference.CcusageThenOAuth,
        UsageProviderPreference.OAuthOnly,
        UsageProviderPreference.CcusageOnly,
    ];

    [ObservableProperty]
    public partial string CcusageCommandOverride { get; set; } = string.Empty;

    /// <summary>Goes through the whole cycle, logs the resolved command line, never spawns Claude.</summary>
    [ObservableProperty]
    public partial bool DryRun { get; set; }

    [ObservableProperty]
    public partial int HistoryRetentionCount { get; set; } = 200;

    /// <summary>The app data root, so the Advanced card can name what it opens.</summary>
    public string AppDataDirectory => _paths.Root;

    /// <summary>
    /// The <c>autoMode.environment</c> entries, one per line, excluding the <c>"$defaults"</c>
    /// sentinel — which is spliced back in on every write and cannot be removed here (plan.md
    /// §5.3.2).
    /// </summary>
    [ObservableProperty]
    public partial string AutoModeEnvironmentText { get; set; } = string.Empty;

    /// <summary>The Claude Code settings file the editor writes.</summary>
    public string AutoModeEnvironmentPath => _autoModeEnvironment.SettingsPath;

    [ObservableProperty]
    public partial string AutoModeEnvironmentStatus { get; private set; } = string.Empty;

    /// <summary>True when a <c>~/.claude.json</c> backup exists to restore (plan.md §11.4b).</summary>
    [ObservableProperty]
    public partial bool HasClaudeJsonBackup { get; private set; }

    /// <summary>Path of that backup, for the button's caption.</summary>
    public string ClaudeJsonBackupPath => _trustManager.BackupPath;

    [ObservableProperty]
    public partial string RestoreBackupStatus { get; private set; } = string.Empty;

    // ── Lifecycle ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads settings from disk and populates everything that is discovered rather than configured:
    /// the detected CLI path, auto-mode availability, the ask rules, the trust keys and the
    /// <c>autoMode.environment</c> list. Never throws.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Apply(await _store.LoadAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not load settings; showing defaults.");
        }

        StartWithWindows = _autorun.IsRegistered;
        HasClaudeJsonBackup = _trustManager.HasBackup;

        await RefreshDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        await LoadAutoModeEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        RefreshPromptPreview();
    }

    /// <summary>
    /// Re-reads everything discovered rather than configured. Cheap enough to run after any change
    /// that could invalidate it (a new project directory, a fixed login).
    /// </summary>
    public async Task RefreshDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        var settings = ToSettings();

        try
        {
            var resolution = _locator.Locate(settings);
            OnUiThread(() =>
            {
                DetectedClaudePath = resolution.IsFound ? resolution.ExecutablePath : string.Empty;
                ClaudeProbedPaths.Clear();
                foreach (var probed in resolution.ProbedPaths)
                {
                    ClaudeProbedPaths.Add(probed);
                }
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not locate the Claude Code executable.");
        }

        try
        {
            var status = await _autoModeProbe.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            AutoModeAvailability = status.Availability;
            AutoModeAvailabilityText = status.Availability switch
            {
                Core.Execution.AutoModeAvailability.Available =>
                    "Auto mode is available; runs use `--permission-mode auto`.",
                Core.Execution.AutoModeAvailability.Unavailable =>
                    status.Detail ?? "Auto mode is unavailable; runs fall back to `acceptEdits`.",
                _ => "Auto mode has not been probed yet.",
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not probe auto mode.");
        }

        try
        {
            var scan = await _permissionsScanner
                .ScanAsync(settings.ProjectDirectory is { Length: > 0 } dir && Directory.Exists(dir) ? dir : null,
                    cancellationToken)
                .ConfigureAwait(false);

            OnUiThread(() =>
            {
                PermissionsAskRules.Clear();
                foreach (var finding in scan.Findings.Where(f => f.Kind != PermissionsFindingKind.Unreadable))
                {
                    PermissionsAskRules.Add(finding.ToString());
                }

                ScannedSettingsFiles.Clear();
                foreach (var file in scan.Files)
                {
                    ScannedSettingsFiles.Add(file.Path);
                }

                OnPropertyChanged(nameof(HasPermissionsAskRules));
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not scan for permissions.ask rules.");
        }

        RefreshTrustKeys();
        HasClaudeJsonBackup = _trustManager.HasBackup;
    }

    /// <summary>
    /// Writes any pending edit immediately and waits for it. Call on window close; a debounce that
    /// is still counting when the process exits would silently lose the last change.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        _debounceTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        if (!HasPendingChanges)
        {
            return;
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _store.SettingsChanged -= _onSettingsChanged;
        _debounceTimer?.Dispose();
        _debounceTimer = null;
        _saveGate.Dispose();
    }

    // ── Commands ───────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    async Task BrowseProjectDirectoryAsync(CancellationToken cancellationToken)
    {
        var picked = await _folderPicker
            .PickFolderAsync("Choose the project directory", ProjectDirectory, cancellationToken)
            .ConfigureAwait(false);

        if (picked is { Length: > 0 })
        {
            ProjectDirectory = picked;
            await RefreshDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    [RelayCommand]
    async Task BrowseClaudeExecutableAsync(CancellationToken cancellationToken)
    {
        var picked = await _filePicker
            .PickFileAsync("Locate the Claude Code executable", null, null, cancellationToken)
            .ConfigureAwait(false);

        if (picked is { Length: > 0 })
        {
            ClaudeExecutablePath = picked;
            await RefreshDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>plan.md §9.2 Prompt: <c>Reset to default</c>.</summary>
    [RelayCommand]
    void ResetPromptTemplate() => PromptTemplate = PilotSettings.DefaultPromptTemplate;

    [RelayCommand]
    void OpenAppDataFolder()
    {
        _paths.EnsureCreated();
        NoticeMessage = _shell.OpenPath(_paths.Root)
            ? string.Empty
            : $"Could not open {_paths.Root}.";
    }

    [RelayCommand]
    void OpenPermissionsSettingsFile(string? path)
    {
        var target = path ?? ScannedSettingsFiles.FirstOrDefault();
        if (target is { Length: > 0 })
        {
            _shell.OpenPath(target);
        }
    }

    /// <summary>plan.md §11.4b: put the user's own <c>~/.claude.json</c> back.</summary>
    [RelayCommand(CanExecute = nameof(HasClaudeJsonBackup))]
    async Task RestoreClaudeJsonBackupAsync(CancellationToken cancellationToken)
    {
        var result = await _trustManager.RestoreBackupAsync(cancellationToken).ConfigureAwait(false);
        RestoreBackupStatus = result.Restored
            ? $"Restored {_trustManager.ConfigPath} from {result.BackupPath}."
            : result.Error ?? "The backup could not be restored.";
        HasClaudeJsonBackup = _trustManager.HasBackup;
    }

    /// <summary>Writes the <c>autoMode.environment</c> list, always splicing in <c>"$defaults"</c>.</summary>
    [RelayCommand]
    async Task SaveAutoModeEnvironmentAsync(CancellationToken cancellationToken)
    {
        var entries = AutoModeEnvironmentText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        var error = await _autoModeEnvironment.WriteAsync(entries, cancellationToken).ConfigureAwait(false);
        AutoModeEnvironmentStatus = error is null
            ? $"Wrote {entries.Length} entr{(entries.Length == 1 ? "y" : "ies")} plus \"$defaults\" to {_autoModeEnvironment.SettingsPath}."
            : $"Could not write {_autoModeEnvironment.SettingsPath}: {error}";
    }

    [RelayCommand]
    async Task ReloadAutoModeEnvironmentAsync(CancellationToken cancellationToken) =>
        await LoadAutoModeEnvironmentAsync(cancellationToken).ConfigureAwait(false);

    // ── Change plumbing ────────────────────────────────────────────────────────────────────────

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName is not { } name || _suppressDepth > 0)
        {
            return;
        }

        if (name is nameof(CavemanLevel) or nameof(PromptTemplate) or nameof(PlanFileName))
        {
            RefreshPromptPreview();
        }

        if (name == nameof(ProjectDirectory))
        {
            RefreshTrustKeys();
        }

        if (name == nameof(StartWithWindows))
        {
            ApplyStartWithWindows();
        }

        if (name == nameof(HasClaudeJsonBackup))
        {
            OnUiThread(RestoreClaudeJsonBackupCommand.NotifyCanExecuteChanged);
        }

        if (PersistedProperties.Contains(name))
        {
            ScheduleSave();
        }
    }

    /// <summary>The settings object the current form state represents, already normalized.</summary>
    public PilotSettings ToSettings() => (_store.Current with
    {
        ProjectDirectory = ProjectDirectory,
        PlanFileName = PlanFileName,
        IntervalMinutes = IntervalMinutes,
        RunOnStartup = RunOnStartup,
        AlignToQuotaReset = AlignToQuotaReset,
        QuotaResetGraceMinutes = QuotaResetGraceMinutes,
        StartWithWindows = StartWithWindows,
        StartMinimized = StartMinimized,
        ThresholdPercent = ThresholdPercent,
        UsageMetric = UsageMetric,
        OnUsageUnavailable = OnUsageUnavailable,
        ClaudeExecutablePath = ClaudeExecutablePath,
        LaunchMode = LaunchMode,
        Model = Model,
        ResumeStrategy = ResumeStrategy,
        MaxRunDurationMinutes = MaxRunDurationMinutes,
        StallTimeoutMinutes = StallTimeoutMinutes,
        PermissionMode = PermissionMode,
        AutoTrustProjectFolder = AutoTrustProjectFolder,
        AllowedTools = AllowedTools,
        SkipPermissionsFallback = SkipPermissionsFallback,
        CavemanLevel = CavemanLevel,
        PromptTemplate = PromptTemplate,
        UsageProviderOrder = UsageProviderOrder,
        CcusageCommandOverride = CcusageCommandOverride,
        DryRun = DryRun,
        HistoryRetentionCount = HistoryRetentionCount,
    }).Normalized();

    /// <summary>Populates the form from <paramref name="settings"/> without scheduling a save.</summary>
    public void Apply(PilotSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var normalized = settings.Normalized();
        _suppressDepth++;
        try
        {
            ProjectDirectory = normalized.ProjectDirectory;
            PlanFileName = normalized.PlanFileName;
            IntervalMinutes = normalized.IntervalMinutes;
            RunOnStartup = normalized.RunOnStartup;
            AlignToQuotaReset = normalized.AlignToQuotaReset;
            QuotaResetGraceMinutes = normalized.QuotaResetGraceMinutes;
            StartMinimized = normalized.StartMinimized;
            ThresholdPercent = normalized.ThresholdPercent;
            UsageMetric = normalized.UsageMetric;
            OnUsageUnavailable = normalized.OnUsageUnavailable;
            ClaudeExecutablePath = normalized.ClaudeExecutablePath;
            LaunchMode = normalized.LaunchMode;
            Model = normalized.Model ?? string.Empty;
            ResumeStrategy = normalized.ResumeStrategy;
            MaxRunDurationMinutes = normalized.MaxRunDurationMinutes;
            StallTimeoutMinutes = normalized.StallTimeoutMinutes;
            PermissionMode = normalized.PermissionMode;
            AutoTrustProjectFolder = normalized.AutoTrustProjectFolder;
            AllowedTools = normalized.AllowedTools;
            SkipPermissionsFallback = normalized.SkipPermissionsFallback;
            CavemanLevel = normalized.CavemanLevel;
            PromptTemplate = normalized.PromptTemplate;
            UsageProviderOrder = normalized.UsageProviderOrder;
            CcusageCommandOverride = normalized.CcusageCommandOverride;
            DryRun = normalized.DryRun;
            HistoryRetentionCount = normalized.HistoryRetentionCount;
        }
        finally
        {
            _suppressDepth--;
        }

        RefreshPromptPreview();
        RefreshTrustKeys();
        OnPropertyChanged(nameof(IsPromptTemplateDefault));
    }

    void ApplyExternalChange(PilotSettings settings)
    {
        // Another writer (a preflight fix picking a directory) changed settings under us. Do not
        // clobber an edit the user is mid-way through typing.
        if (HasPendingChanges)
        {
            return;
        }

        Apply(settings);
    }

    void ScheduleSave()
    {
        HasPendingChanges = true;
        SaveError = string.Empty;

        _debounceTimer ??= _time.CreateTimer(
            _ => _dispatcher.Post(() => _ = SaveAsync(CancellationToken.None)),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);

        _debounceTimer.Change(DebounceInterval, Timeout.InfiniteTimeSpan);
    }

    async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = ToSettings();
            await _store.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
            HasPendingChanges = false;
            LastSavedAt = _time.GetUtcNow();
            SaveError = string.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not save settings.");
            SaveError = ex.Message;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    void ApplyStartWithWindows()
    {
        if (!_autorun.IsSupported)
        {
            return;
        }

        if (_autorun.Apply(StartWithWindows))
        {
            StartWithWindowsError = string.Empty;
            return;
        }

        // The registry refused. Put the toggle back rather than let it assert something untrue.
        StartWithWindowsError = StartWithWindows
            ? "Could not add NightShift to the Windows startup list."
            : "Could not remove NightShift from the Windows startup list.";

        _suppressDepth++;
        try
        {
            StartWithWindows = !StartWithWindows;
        }
        finally
        {
            _suppressDepth--;
        }
    }

    void RefreshPromptPreview()
    {
        PromptPreview = PromptBuilder.Build(CavemanLevel, PromptTemplate, PlanFileName);
        OnPropertyChanged(nameof(IsPromptTemplateDefault));
    }

    void RefreshTrustKeys()
    {
        TrustKeysPreview.Clear();
        if (ProjectDirectory.Length == 0)
        {
            return;
        }

        try
        {
            foreach (var key in WorkspaceTrustManager.TrustKeysFor(ProjectDirectory))
            {
                TrustKeysPreview.Add(key);
            }
        }
        catch (ArgumentException ex)
        {
            _logger.LogDebug(ex, "Could not compute trust keys for {Directory}.", ProjectDirectory);
        }
    }

    async Task LoadAutoModeEnvironmentAsync(CancellationToken cancellationToken)
    {
        var environment = await _autoModeEnvironment.ReadAsync(cancellationToken).ConfigureAwait(false);

        _suppressDepth++;
        try
        {
            AutoModeEnvironmentText = string.Join('\n', environment.Entries);
        }
        finally
        {
            _suppressDepth--;
        }

        AutoModeEnvironmentStatus = environment.Error is { } error
            ? $"Could not read {_autoModeEnvironment.SettingsPath}: {error}"
            : string.Empty;
    }

    /// <summary>Display labels for the enum dropdowns, so a view converter never invents its own.</summary>
    public static string Describe(UsageMetric metric) => metric switch
    {
        UsageMetric.HighestOfAll => "Highest of all",
        UsageMetric.FiveHour => "Session (5h)",
        UsageMetric.SevenDay => "Weekly (7d)",
        _ => metric.ToString(),
    };

    /// <inheritdoc cref="Describe(UsageMetric)"/>
    public static string Describe(UsageUnavailableBehavior behavior) => behavior switch
    {
        UsageUnavailableBehavior.Skip => "Skip the cycle",
        UsageUnavailableBehavior.Run => "Run anyway",
        _ => behavior.ToString(),
    };

    /// <inheritdoc cref="Describe(UsageMetric)"/>
    public static string Describe(LaunchMode mode) => mode switch
    {
        LaunchMode.Headless => "Headless (logged)",
        LaunchMode.VisibleTerminal => "Visible terminal",
        _ => mode.ToString(),
    };

    /// <inheritdoc cref="Describe(UsageMetric)"/>
    public static string Describe(ResumeStrategy strategy) => strategy switch
    {
        ResumeStrategy.Fresh => "Fresh session each run",
        ResumeStrategy.Resume => "Resume the previous session",
        _ => strategy.ToString(),
    };

    /// <inheritdoc cref="Describe(UsageMetric)"/>
    public static string Describe(PermissionMode mode) => mode switch
    {
        PermissionMode.Auto => "auto — everything runs, a classifier blocks destructive actions",
        PermissionMode.AcceptEdits => "acceptEdits — edits auto-approved, needs a broad tool list",
        PermissionMode.BypassPermissions => "bypassPermissions — no checking at all, isolated repos only",
        _ => mode.ToString(),
    };

    /// <inheritdoc cref="Describe(UsageMetric)"/>
    public static string Describe(CavemanLevel level) =>
        level.ToCommandArgument();

    /// <inheritdoc cref="Describe(UsageMetric)"/>
    public static string Describe(UsageProviderPreference preference) => preference switch
    {
        UsageProviderPreference.OAuthThenCcusage => "OAuth, then ccusage",
        UsageProviderPreference.CcusageThenOAuth => "ccusage, then OAuth",
        UsageProviderPreference.OAuthOnly => "OAuth only",
        UsageProviderPreference.CcusageOnly => "ccusage only",
        _ => preference.ToString(),
    };

    /// <summary>Minutes rendered for a read-only caption, e.g. <c>55 minutes</c>.</summary>
    public static string DescribeMinutes(int minutes) =>
        minutes == 1 ? "1 minute" : minutes.ToString(CultureInfo.InvariantCulture) + " minutes";
}
