using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NightShift.Core.Configuration;
using NightShift.Core.Execution;
using NightShift.Core.Io;
using NightShift.Core.Markdown;
using NightShift.Core.Preflight;
using NightShift.Core.Scheduling;
using NightShift.Desktop.Services;

namespace NightShift.Desktop.ViewModels;

/// <summary>
/// The plan window: read the plan rendered, edit it, save it back, or hand it to Claude.
/// </summary>
/// <remarks>
/// <para>
/// The file this shows is the one thing a run is also writing to, which shapes almost every
/// decision here. Saving is explicit and never on a timer; a change arriving from disk reloads
/// silently only when there is nothing of the user's to lose; and the window says out loud when a
/// run is working, because two writers and no warning is how work disappears.
/// </para>
/// <para>
/// Named for <see cref="ViewLocator"/>, which replaces <c>ViewModel</c> with <c>View</c> across the
/// whole type name — so this resolves to <c>Views.PlanDocumentView</c>.
/// </para>
/// </remarks>
public sealed partial class PlanDocumentViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// How long to wait after a file-change notification before acting on it.
    /// </summary>
    /// <remarks>
    /// Writers emit bursts, and <see cref="AtomicFile"/>'s own temp-write-then-rename is two events
    /// by itself. Re-armed per event, exactly as <see cref="SettingsViewModel"/> debounces a save.
    /// </remarks>
    public static readonly TimeSpan DefaultWatchDebounce = TimeSpan.FromMilliseconds(400);

    readonly ISettingsStore _settings;
    readonly PilotScheduler _scheduler;
    readonly IFileWatcher _watcher;
    readonly IConfirmationService _confirmation;
    readonly IClipboard _clipboard;
    readonly IClaudeExecutableLocator _locator;
    readonly IClaudeTerminalLauncher _terminal;
    readonly IUiDispatcher _dispatcher;
    readonly ILogger<PlanDocumentViewModel> _logger;
    readonly TimeProvider _time;
    readonly TimeSpan _watchDebounce;

    readonly EventHandler<CycleCompleted> _onCycleCompleted;
    readonly EventHandler<RunProgress> _onRunProgress;
    readonly EventHandler<PilotSettings> _onSettingsChanged;
    readonly EventHandler _onFileChanged;

    ITimer? _debounceTimer;

    /// <summary>What was last read from or written to disk, LF-normalised. The dirty check's other half.</summary>
    string _diskText = string.Empty;

    /// <summary>Encoding, line endings and trailing newline, so a save puts the file back as it was.</summary>
    TextFileShape _shape = TextFileShape.Default;

    bool _disposed;

    public PlanDocumentViewModel(
        ISettingsStore settings,
        PilotScheduler scheduler,
        IFileWatcher watcher,
        IConfirmationService confirmation,
        IClipboard clipboard,
        IClaudeExecutableLocator locator,
        IClaudeTerminalLauncher terminal,
        IUiDispatcher dispatcher,
        ILogger<PlanDocumentViewModel> logger,
        TimeProvider? timeProvider = null,
        TimeSpan? watchDebounce = null)
        : base(dispatcher)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = timeProvider ?? TimeProvider.System;
        _watchDebounce = watchDebounce ?? DefaultWatchDebounce;

        _onCycleCompleted = (_, _) => _dispatcher.Post(() => IsRunning = false);
        _onRunProgress = (_, _) => _dispatcher.Post(() => IsRunning = true);
        _onSettingsChanged = (_, updated) => _dispatcher.Post(() => ApplySettings(updated));
        _onFileChanged = (_, _) => ScheduleReload();

        _scheduler.CycleCompleted += _onCycleCompleted;
        _scheduler.RunProgress += _onRunProgress;
        _settings.SettingsChanged += _onSettingsChanged;
        _watcher.Changed += _onFileChanged;

        ApplySettings(_settings.Current);
        IsRunning = _scheduler.IsRunning;
    }

    /// <summary>Raised after the user saves, so the dashboard can refresh the card's tally.</summary>
    public event EventHandler? Saved;

    // ── State ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The resolved plan path, or empty when there is no project directory.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlanPath))]
    public partial string PlanPath { get; private set; } = string.Empty;

    public bool HasPlanPath => PlanPath.Length > 0;

    [ObservableProperty]
    public partial string PlanFileName { get; private set; } = "plan.md";

    /// <summary>The parsed document behind the rendered view. Null before the first load.</summary>
    [ObservableProperty]
    public partial MarkdownDocument? Document { get; private set; }

    /// <summary>The editor buffer, LF-normalised.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string RawText { get; set; } = string.Empty;

    /// <summary>True while the raw editor is showing instead of the rendered view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReading))]
    [NotifyCanExecuteChangedFor(nameof(BeginEditCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsEditing { get; private set; }

    public bool IsReading => !IsEditing;

    /// <summary>The buffer differs from what is on disk.</summary>
    public bool IsDirty => IsEditing && !string.Equals(RawText, _diskText, StringComparison.Ordinal);

    /// <summary>The file changed underneath an edit and the user has not said what to do about it.</summary>
    [ObservableProperty]
    public partial bool HasConflict { get; private set; }

    /// <summary>What the two versions each say, so the notice can be specific rather than vague.</summary>
    [ObservableProperty]
    public partial string ConflictDetail { get; private set; } = string.Empty;

    /// <summary>A run is working in this project right now.</summary>
    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    /// <summary>One line about what just happened. Empty most of the time.</summary>
    [ObservableProperty]
    public partial string StatusMessage { get; private set; } = string.Empty;

    /// <summary>Why the plan could not be read or written. Empty when all is well.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorMessage { get; private set; } = string.Empty;

    public bool HasError => ErrorMessage.Length > 0;

    /// <summary>The tally, shown beside the document so the window and the card agree out loud.</summary>
    [ObservableProperty]
    public partial string ItemSummary { get; private set; } = string.Empty;

    // ── Loading ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Reads the plan from disk and starts watching it.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!HasPlanPath)
        {
            Document = null;
            RawText = string.Empty;
            _diskText = string.Empty;
            ItemSummary = string.Empty;
            ErrorMessage = "No project directory has been set, so there is no plan to show. " +
                "Choose one in Settings › Project.";
            return;
        }

        try
        {
            var (text, shape) = await TextFileShape.ReadAsync(PlanPath, cancellationToken)
                .ConfigureAwait(false);

            OnUiThread(() => Apply(text, shape));
        }
        catch (FileNotFoundException)
        {
            OnUiThread(() => Missing());
        }
        catch (DirectoryNotFoundException)
        {
            OnUiThread(() => Missing());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read {Path}.", PlanPath);
            OnUiThread(() => ErrorMessage = $"{PlanPath} could not be read: {ex.Message}");
        }

        _watcher.Watch(PlanPath);
    }

    void Apply(string text, TextFileShape shape)
    {
        _shape = shape;
        _diskText = text;
        RawText = text;
        Document = MarkdownReader.Read(text);
        ErrorMessage = string.Empty;
        HasConflict = false;
        ConflictDetail = string.Empty;
        ItemSummary = PlanParser.Parse(text, _settings.Current.PlanFormat).Counts.Summary;
        OnPropertyChanged(nameof(IsDirty));
    }

    void Missing()
    {
        Document = null;
        RawText = string.Empty;
        _diskText = string.Empty;
        ItemSummary = string.Empty;
        ErrorMessage = $"There is no {PlanFileName} in this project yet.";
    }

    // ── Editing ────────────────────────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanBeginEdit))]
    void BeginEdit()
    {
        IsEditing = true;
        StatusMessage = IsRunning
            ? "A run is working in this project right now — save carefully."
            : string.Empty;
    }

    bool CanBeginEdit() => !IsEditing && HasPlanPath;

    /// <summary>Leaves the editor, discarding the buffer once the user has confirmed.</summary>
    [RelayCommand]
    async Task CancelEditAsync(CancellationToken cancellationToken)
    {
        if (IsDirty && !await ConfirmDiscardAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        OnUiThread(() =>
        {
            RawText = _diskText;
            IsEditing = false;
            HasConflict = false;
            StatusMessage = string.Empty;
        });
    }

    Task<bool> ConfirmDiscardAsync(CancellationToken cancellationToken) =>
        _confirmation.ConfirmAsync(
            "Discard your changes?",
            $"Your edits to {PlanFileName} have not been saved. Closing now loses them.",
            "Discard them",
            cancellationToken);

    [RelayCommand(CanExecute = nameof(CanSave))]
    async Task SaveAsync(CancellationToken cancellationToken)
    {
        var text = RawText;

        try
        {
            // Keeping the file's own encoding, line endings and trailing newline is what stops a
            // two-line edit reporting as a whole-file diff in the user's next commit.
            await _shape.WriteAsync(PlanPath, text, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save {Path}.", PlanPath);

            // The buffer is deliberately left alone: a failed write must never also lose the text.
            OnUiThread(() => ErrorMessage = $"{PlanPath} could not be saved: {ex.Message}");
            return;
        }

        OnUiThread(() =>
        {
            _diskText = _shape.Apply(text).Replace(_shape.Newline, "\n", StringComparison.Ordinal);
            Document = MarkdownReader.Read(text);
            ItemSummary = PlanParser.Parse(text, _settings.Current.PlanFormat).Counts.Summary;
            IsEditing = false;
            HasConflict = false;
            ConflictDetail = string.Empty;
            ErrorMessage = string.Empty;
            StatusMessage = $"Saved {PlanFileName}.";
            OnPropertyChanged(nameof(IsDirty));
        });

        // The card's counts move immediately rather than at the next cycle: ticking a box and
        // watching the dashboard not change is what makes the two look unrelated.
        Saved?.Invoke(this, EventArgs.Empty);
    }

    bool CanSave() => IsEditing && HasPlanPath;

    /// <summary>
    /// Writes an unsaved buffer out at shutdown only when the user says so.
    /// </summary>
    /// <remarks>
    /// Settings flush silently on exit because they are small and reversible. A plan is neither: it
    /// is a document a background run is also writing to, and silently saving over what that run
    /// just committed is the one outcome nobody could undo.
    /// </remarks>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (!IsDirty)
        {
            return;
        }

        var save = await _confirmation.ConfirmAsync(
            "Save your changes?",
            $"You have unsaved changes to {PlanFileName}. Save them before NightShift closes?",
            "Save them",
            cancellationToken).ConfigureAwait(false);

        if (save)
        {
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // ── Watching and conflicts ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The reload the debounce timer last started. Exposed so a test can await the work the timer
    /// kicked off rather than sleep and hope.
    /// </summary>
    internal Task PendingReload { get; private set; } = Task.CompletedTask;

    void ScheduleReload()
    {
        _debounceTimer ??= _time.CreateTimer(
            _ => _dispatcher.Post(() => PendingReload = ReloadFromDiskAsync(CancellationToken.None)),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);

        _debounceTimer.Change(_watchDebounce, Timeout.InfiniteTimeSpan);
    }

    async Task ReloadFromDiskAsync(CancellationToken cancellationToken)
    {
        if (!HasPlanPath)
        {
            return;
        }

        string text;
        TextFileShape shape;

        try
        {
            (text, shape) = await TextFileShape.ReadAsync(PlanPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or FileNotFoundException or DirectoryNotFoundException)
        {
            // The event can land mid-rename while another process still holds the handle. Treating
            // that as "nothing happened" is better than showing an empty document.
            _logger.LogDebug(ex, "Ignoring a change notification that could not be read.");
            return;
        }

        OnUiThread(() =>
        {
            // Our own save, coming back to us. Comparing content rather than timestamps is what
            // makes this exact instead of a race.
            if (string.Equals(text, _diskText, StringComparison.Ordinal))
            {
                return;
            }

            if (!IsDirty)
            {
                Apply(text, shape);
                StatusMessage = $"Reloaded — {PlanFileName} changed on disk.";
                return;
            }

            _shape = shape;
            HasConflict = true;
            ConflictDetail = Describe(text);
            StatusMessage = string.Empty;
        });
    }

    /// <summary>
    /// What each version says, in counts. One line of arithmetic that answers the question a diff
    /// view would have answered slower and worse.
    /// </summary>
    string Describe(string theirs)
    {
        var format = _settings.Current.PlanFormat;
        var mine = PlanParser.Parse(RawText, format).Counts;
        var disk = PlanParser.Parse(theirs, format).Counts;

        return $"The version on disk has {disk.Summary}. Yours has {mine.Summary}.";
    }

    /// <summary>Dismiss the conflict and keep editing; the next save wins.</summary>
    [RelayCommand]
    void KeepMine()
    {
        HasConflict = false;
        StatusMessage = "Keeping your version. Saving will overwrite what is on disk.";
    }

    /// <summary>Throw the buffer away and take what is on disk.</summary>
    [RelayCommand]
    async Task LoadTheirsAsync(CancellationToken cancellationToken)
    {
        if (!await ConfirmDiscardAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        HasConflict = false;
        await LoadAsync(cancellationToken).ConfigureAwait(false);
        OnUiThread(() => StatusMessage = $"Loaded the version from disk.");
    }

    // ── Edit with Claude ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens an attended Claude session in plan mode, in the project directory.
    /// </summary>
    /// <remarks>
    /// Plan mode is exactly what an unattended run must never use, because it ends by waiting for a
    /// human to approve. Here a human is the point. It is deliberately not a NightShift run: no run
    /// gate, no history row, no transcript — the History tab keeps meaning one thing, which is
    /// unattended cycles. Workspace trust is not applied either; an attended user can answer the
    /// trust dialog themselves, which avoids writing their global config outside the run gate.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(HasPlanPath))]
    async Task EditWithClaudeAsync(CancellationToken cancellationToken)
    {
        var settings = _settings.Current;

        var resolution = _locator.Locate(settings);
        if (!resolution.IsFound)
        {
            ErrorMessage = resolution.FailureReason
                ?? "Claude Code could not be found. Set its path in Settings › Claude.";
            return;
        }

        if (IsDirty)
        {
            var save = await _confirmation.ConfirmAsync(
                "Save first?",
                $"Claude will read {PlanFileName} from disk, which does not yet include your " +
                "unsaved changes.",
                "Save and open",
                cancellationToken).ConfigureAwait(false);

            if (!save)
            {
                return;
            }

            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }

        if (IsRunning)
        {
            var proceed = await _confirmation.ConfirmAsync(
                "A run is working here",
                "A run is working in this project right now. Opening a second Claude here means " +
                "two of them could edit the plan at the same time.",
                "Open anyway",
                cancellationToken).ConfigureAwait(false);

            if (!proceed)
            {
                return;
            }
        }

        var format = PlanParser.Parse(RawText, settings.PlanFormat).Format;

        // The full conventions go to the clipboard, where nothing reinterprets them; the terminal
        // gets a version with no character cmd.exe would eat.
        var copied = await CopyConventionsAsync(format, cancellationToken).ConfigureAwait(false);

        var launched = _terminal.TryLaunch(
            resolution.ExecutablePath,
            settings.ProjectDirectory,
            PlanModeCliValue,
            SeedPromptFor(format, PlanFileName),
            extraArguments: null,
            out var error);

        OnUiThread(() =>
        {
            if (!launched)
            {
                ErrorMessage = error ?? "Could not open a terminal.";
                return;
            }

            ErrorMessage = string.Empty;
            StatusMessage = copied
                ? "Claude opened in plan mode. The plan's conventions are on your clipboard."
                : "Claude opened in plan mode.";
        });
    }

    async Task<bool> CopyConventionsAsync(PlanFormat format, CancellationToken cancellationToken)
    {
        try
        {
            return await _clipboard
                .TrySetTextAsync(PromptBuilder.ConventionsFor(format, PlanFileName), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not copy the plan conventions to the clipboard.");
            return false;
        }
    }

    /// <summary>The literal <c>--permission-mode</c> value for an attended planning session.</summary>
    /// <remarks>
    /// A string rather than a <see cref="PermissionMode"/> because that enum deliberately has no
    /// <c>Plan</c> member — see <see cref="IClaudeTerminalLauncher"/>.
    /// </remarks>
    internal const string PlanModeCliValue = "plan";

    /// <summary>
    /// The opening prompt, as one line safe to hand to <c>cmd /k</c>.
    /// </summary>
    /// <remarks>
    /// It must contain none of <c>%</c>, <c>!</c>, <c>"</c>, CR or LF — which is why it says
    /// "an exclamation mark" rather than showing <c>- [!]</c>, and why the literal marker text goes
    /// to the clipboard instead. <c>ClaudeTerminalLauncher</c> drops a hazardous prompt rather than
    /// mangling it, and a test pins that this one never is.
    /// </remarks>
    internal static string SeedPromptFor(PlanFormat format, string planFileName) =>
        format == PlanFormat.Milestone
            ? $"Read {planFileName} in this project and help me edit it. It is a milestone plan: " +
              "milestones are headings numbered M1, M2 and so on, and delivered ones are marked " +
              "delivered in bold. Do not change anything until I approve it."
            : $"Read {planFileName} in this project and help me edit it. It is a checkbox task " +
              "list: an empty box is open work, an x means done and an exclamation mark means " +
              "blocked. Do not change anything until I approve it.";

    // ── Settings ───────────────────────────────────────────────────────────────────────────────

    void ApplySettings(PilotSettings settings)
    {
        var normalized = settings.Normalized();
        PlanFileName = normalized.PlanFileName;

        var path = settings.ResolvePlanPath() ?? string.Empty;
        if (string.Equals(path, PlanPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        PlanPath = path;
        BeginEditCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        EditWithClaudeCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _scheduler.CycleCompleted -= _onCycleCompleted;
        _scheduler.RunProgress -= _onRunProgress;
        _settings.SettingsChanged -= _onSettingsChanged;
        _watcher.Changed -= _onFileChanged;
        _watcher.Dispose();
        _debounceTimer?.Dispose();
        _debounceTimer = null;
    }
}
