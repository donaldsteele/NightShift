using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NightShift.Core.Execution;
using NightShift.Core.History;
using NightShift.Core.Scheduling;
using NightShift.Desktop.Services;

namespace NightShift.Desktop.ViewModels;

/// <summary>
/// The run list, its outcome filter chips, and the read-only transcript pane with search
/// (plan.md §9.3).
/// </summary>
/// <remarks>
/// Refreshes itself when a cycle completes, so a run that finishes while the History tab is open
/// appears without the user going back and forth. The scheduler raises that on a background thread,
/// hence the <see cref="IUiDispatcher"/> hop.
/// </remarks>
public sealed partial class HistoryViewModel : ViewModelBase, IDisposable
{
    /// <summary>Every outcome, in the order the filter chips appear.</summary>
    public static readonly IReadOnlyList<RunOutcome> AllOutcomes =
    [
        RunOutcome.Success,
        RunOutcome.Failed,
        RunOutcome.TimedOut,
        RunOutcome.RateLimited,
        RunOutcome.Skipped,
        RunOutcome.Launched,
    ];

    readonly RunHistoryStore _history;
    readonly IRunHistoryEditor _editor;
    readonly PilotScheduler _scheduler;
    readonly IUiDispatcher _dispatcher;
    readonly IShellLauncher _shell;
    readonly IClipboard _clipboard;
    readonly ILogger<HistoryViewModel> _logger;
    readonly EventHandler<CycleCompleted> _onCycleCompleted;
    readonly List<RunRowViewModel> _allRuns = [];

    bool _disposed;

    public HistoryViewModel(
        RunHistoryStore history,
        IRunHistoryEditor editor,
        PilotScheduler scheduler,
        IUiDispatcher dispatcher,
        IShellLauncher shell,
        IClipboard clipboard,
        ILogger<HistoryViewModel> logger)
    : base(dispatcher)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        foreach (var outcome in AllOutcomes)
        {
            OutcomeFilters.Add(new OutcomeFilterViewModel(outcome, ApplyFilter));
        }

        _onCycleCompleted = (_, _) => _dispatcher.Post(() => _ = RefreshAsync(CancellationToken.None));
        _scheduler.CycleCompleted += _onCycleCompleted;
    }

    /// <summary>The visible rows, newest first, after the outcome filter.</summary>
    public ObservableCollection<RunRowViewModel> Runs { get; } = [];

    /// <summary>The filter chips, one per <see cref="RunOutcome"/> including <c>RateLimited</c>.</summary>
    public ObservableCollection<OutcomeFilterViewModel> OutcomeFilters { get; } = [];

    /// <summary>Total runs loaded, before filtering.</summary>
    [ObservableProperty]
    public partial int TotalRunCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    [NotifyCanExecuteChangedFor(nameof(ClearOutcomeFiltersCommand))]
    public partial int ActiveFilterCount { get; private set; }

    /// <summary>True when at least one chip is on; with none on, everything is shown.</summary>
    public bool HasActiveFilters => ActiveFilterCount > 0;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>True when the grid has nothing to show, so the view can offer an empty state.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; } = true;

    /// <summary>What the last action did, for a status line under the grid.</summary>
    [ObservableProperty]
    public partial string StatusMessage { get; private set; } = string.Empty;

    // ── Selection and the transcript pane ──────────────────────────────────────────────────────

    /// <summary>
    /// The selected row. Setting it loads that run's transcript from disk (plan.md §9.3:
    /// "selecting a row opens the transcript in a read-only pane with search").
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenTranscriptFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopySessionIdCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteRunCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial RunRowViewModel? SelectedRun { get; set; }

    public bool HasSelection => SelectedRun is not null;

    /// <summary>The selected run's full transcript. Empty when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTranscript))]
    public partial string TranscriptText { get; private set; } = string.Empty;

    public bool HasTranscript => TranscriptText.Length > 0;

    [ObservableProperty]
    public partial bool IsTranscriptLoading { get; private set; }

    /// <summary>Case-insensitive search over <see cref="TranscriptText"/>.</summary>
    [ObservableProperty]
    public partial string TranscriptSearchText { get; set; } = string.Empty;

    /// <summary>Character offsets of every match, in order.</summary>
    public IReadOnlyList<int> TranscriptMatchOffsets { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTranscriptMatches))]
    [NotifyCanExecuteChangedFor(nameof(NextMatchCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousMatchCommand))]
    public partial int TranscriptMatchCount { get; private set; }

    public bool HasTranscriptMatches => TranscriptMatchCount > 0;

    /// <summary>Zero-based index of the highlighted match; -1 when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedMatchOffset))]
    [NotifyPropertyChangedFor(nameof(TranscriptMatchPositionText))]
    public partial int SelectedMatchIndex { get; private set; } = -1;

    /// <summary>Character offset of the highlighted match, for the view to select and scroll to.</summary>
    public int SelectedMatchOffset =>
        SelectedMatchIndex >= 0 && SelectedMatchIndex < TranscriptMatchOffsets.Count
            ? TranscriptMatchOffsets[SelectedMatchIndex]
            : -1;

    /// <summary>Length of the highlighted match — the search term's length.</summary>
    public int SelectedMatchLength => TranscriptSearchText.Length;

    /// <summary><c>3 of 17</c>, or empty when nothing is being searched.</summary>
    public string TranscriptMatchPositionText =>
        TranscriptMatchCount == 0 || SelectedMatchIndex < 0
            ? string.Empty
            : $"{SelectedMatchIndex + 1} of {TranscriptMatchCount}";

    // ── Lifecycle ──────────────────────────────────────────────────────────────────────────────

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        RefreshAsync(cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _scheduler.CycleCompleted -= _onCycleCompleted;
    }

    // ── Commands ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Re-reads <c>runs/index.jsonl</c> and rebuilds the grid, preserving the selection.</summary>
    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        IsLoading = true;
        try
        {
            IReadOnlyList<RunRecord> records;
            try
            {
                records = await _history.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not read the run history.");
                StatusMessage = $"Could not read the run history: {ex.Message}";
                return;
            }

            var selectedId = SelectedRun?.Id;

            _allRuns.Clear();
            // Newest first: the index is append-only and therefore chronological.
            for (var i = records.Count - 1; i >= 0; i--)
            {
                _allRuns.Add(new RunRowViewModel(records[i], _history.TranscriptPath(records[i].Id)));
            }

            TotalRunCount = _allRuns.Count;

            foreach (var filter in OutcomeFilters)
            {
                filter.Count = _allRuns.Count(run => run.Outcome == filter.Outcome);
            }

            ApplyFilter();

            if (selectedId is { Length: > 0 })
            {
                SelectedRun = Runs.FirstOrDefault(run => run.Id == selectedId);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>plan.md §9.3 right-click: <c>Open transcript file</c>.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    void OpenTranscriptFile()
    {
        if (SelectedRun is not { } run)
        {
            return;
        }

        StatusMessage = _shell.OpenPath(run.TranscriptPath)
            ? $"Opened {run.TranscriptPath}."
            : $"Could not open {run.TranscriptPath}.";
    }

    /// <summary>plan.md §9.3 right-click: <c>Copy session id</c>.</summary>
    [RelayCommand(CanExecute = nameof(CanCopySessionId))]
    async Task CopySessionIdAsync(CancellationToken cancellationToken)
    {
        if (SelectedRun?.SessionId is not { Length: > 0 } sessionId)
        {
            return;
        }

        var copied = await _clipboard.TrySetTextAsync(sessionId, cancellationToken).ConfigureAwait(false);
        StatusMessage = copied ? "Session id copied." : "Could not copy to the clipboard.";
    }

    bool CanCopySessionId => SelectedRun?.HasSessionId ?? false;

    /// <summary>plan.md §9.3 right-click: <c>Delete</c>. Removes the index line and the transcript.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    async Task DeleteRunAsync(CancellationToken cancellationToken)
    {
        if (SelectedRun is not { } run)
        {
            return;
        }

        var deleted = await _editor.DeleteAsync(run.Id, cancellationToken).ConfigureAwait(false);
        StatusMessage = deleted ? $"Deleted run {run.Id}." : $"Could not delete run {run.Id}.";

        if (deleted)
        {
            SelectedRun = null;
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    [RelayCommand(CanExecute = nameof(HasActiveFilters))]
    void ClearOutcomeFilters()
    {
        foreach (var filter in OutcomeFilters)
        {
            filter.IsSelected = false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasTranscriptMatches))]
    void NextMatch()
    {
        if (TranscriptMatchCount == 0)
        {
            return;
        }

        SelectedMatchIndex = (SelectedMatchIndex + 1) % TranscriptMatchCount;
    }

    [RelayCommand(CanExecute = nameof(HasTranscriptMatches))]
    void PreviousMatch()
    {
        if (TranscriptMatchCount == 0)
        {
            return;
        }

        SelectedMatchIndex = (SelectedMatchIndex - 1 + TranscriptMatchCount) % TranscriptMatchCount;
    }

    // ── Internals ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds <see cref="Runs"/> from the loaded records. No chip selected means "show
    /// everything"; otherwise a run must match one of the selected outcomes.
    /// </summary>
    void ApplyFilter()
    {
        var selected = OutcomeFilters.Where(filter => filter.IsSelected).Select(filter => filter.Outcome).ToArray();
        ActiveFilterCount = selected.Length;

        // Bound collection: rebuilding it off the UI thread replays the queued Adds after the Reset
        // and doubles the list (see ViewModelBase.OnUiThread).
        OnUiThread(() =>
        {
            Runs.Clear();
            foreach (var run in _allRuns)
            {
                if (selected.Length == 0 || Array.IndexOf(selected, run.Outcome) >= 0)
                {
                    Runs.Add(run);
                }
            }

            IsEmpty = Runs.Count == 0;
        });
    }

    partial void OnSelectedRunChanged(RunRowViewModel? value)
    {
        OnUiThread(CopySessionIdCommand.NotifyCanExecuteChanged);
        _ = LoadTranscriptAsync(value);
    }

    partial void OnTranscriptSearchTextChanged(string value) => RecomputeMatches();

    async Task LoadTranscriptAsync(RunRowViewModel? run)
    {
        if (run is null)
        {
            TranscriptText = string.Empty;
            RecomputeMatches();
            return;
        }

        IsTranscriptLoading = true;
        try
        {
            TranscriptText = await _history.ReadTranscriptAsync(run.Id).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read the transcript for {RunId}.", run.Id);
            TranscriptText = $"Could not read {run.TranscriptPath}: {ex.Message}";
        }
        finally
        {
            IsTranscriptLoading = false;
            RecomputeMatches();
        }
    }

    void RecomputeMatches()
    {
        var term = TranscriptSearchText;
        if (term.Length == 0 || TranscriptText.Length == 0)
        {
            TranscriptMatchOffsets = [];
            TranscriptMatchCount = 0;
            SelectedMatchIndex = -1;
            OnPropertyChanged(nameof(SelectedMatchLength));
            OnPropertyChanged(nameof(TranscriptMatchPositionText));
            return;
        }

        var offsets = new List<int>();
        var index = TranscriptText.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            offsets.Add(index);
            index = TranscriptText.IndexOf(term, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        TranscriptMatchOffsets = offsets;
        TranscriptMatchCount = offsets.Count;
        SelectedMatchIndex = offsets.Count > 0 ? 0 : -1;
        OnPropertyChanged(nameof(SelectedMatchLength));
        OnPropertyChanged(nameof(SelectedMatchOffset));
        OnPropertyChanged(nameof(TranscriptMatchPositionText));
    }
}
