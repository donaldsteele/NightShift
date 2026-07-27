using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NightShift.Core.Configuration;
using NightShift.Core.History;
using NightShift.Core.Usage;

namespace NightShift.Desktop.ViewModels;

/// <summary>
/// One row of the history grid: started, duration, outcome badge, usage at start, cost, summary
/// (plan.md §9.3).
/// </summary>
/// <remarks>
/// Immutable — a <see cref="RunRecord"/> is a finished fact. <see cref="HistoryViewModel"/> rebuilds
/// rows on refresh rather than mutating them.
/// </remarks>
public sealed class RunRowViewModel : ViewModelBase
{
    public RunRowViewModel(RunRecord record, string transcriptPath)
    {
        Record = record ?? throw new ArgumentNullException(nameof(record));
        TranscriptPath = record.TranscriptPath ?? transcriptPath;
    }

    public RunRecord Record { get; }

    public string Id => Record.Id;

    public DateTimeOffset StartedAt => Record.StartedAt;

    /// <summary>Local wall-clock start, e.g. <c>2026-07-26 02:14</c>.</summary>
    public string StartedAtText =>
        Record.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public TimeSpan? Duration => Record.Duration;

    /// <summary>Wall-clock length, or an em dash for a run that never ended.</summary>
    public string DurationText => Record.Duration is { } duration
        ? TimeSpanText.DescribeElapsed(duration)
        : "—";

    public RunOutcome Outcome => Record.Outcome;

    /// <summary>Badge text. <c>RateLimited</c> reads as "Rate limited".</summary>
    public string OutcomeText => DashboardViewModel.DescribeOutcome(Record.Outcome);

    /// <summary>True for the plan.md §6.1 outcome: cut short by quota, resumable, nothing broken.</summary>
    public bool IsRateLimited => Record.Outcome == RunOutcome.RateLimited;

    public SkipReason SkipReason => Record.SkipReason;

    /// <summary>Why a skip happened, for the tooltip on a <c>Skipped</c> badge.</summary>
    public string? SkipDetail => Record.SkipDetail;

    public UsageSnapshot? UsageAtStart => Record.UsageAtStart;

    /// <summary>
    /// The usage figure the decision was made from, e.g. <c>72% (5h) · 41% (7d)</c>, with an
    /// "approx" marker when it came from <c>ccusage</c>.
    /// </summary>
    public string UsageAtStartText
    {
        get
        {
            if (Record.UsageAtStart is not { IsAvailable: true } usage)
            {
                return "—";
            }

            var parts = new List<string>(2);
            if (usage.FiveHour is { } fiveHour)
            {
                parts.Add(Percent(fiveHour.UtilizationPercent) + " (5h)");
            }

            if (usage.SevenDay is { } sevenDay)
            {
                parts.Add(Percent(sevenDay.UtilizationPercent) + " (7d)");
            }

            if (parts.Count == 0)
            {
                return "—";
            }

            return string.Join(" · ", parts) + (usage.IsApproximate ? " approx" : string.Empty);
        }
    }

    public double? CostUsd => Record.CostUsd;

    /// <summary>Spend for the run, e.g. <c>$0.42</c>. An em dash when the CLI reported none.</summary>
    public string CostText => Record.CostUsd is { } cost
        ? "$" + cost.ToString("0.00", CultureInfo.InvariantCulture)
        : "—";

    /// <summary>Last assistant text, already truncated to 500 characters by Core.</summary>
    public string Summary => Record.Summary ?? string.Empty;

    public string? SessionId => Record.SessionId;

    public bool HasSessionId => Record.SessionId is { Length: > 0 };

    /// <summary>
    /// Which <c>--permission-mode</c> the run really used. plan.md §11.4c requires this be visible:
    /// the fallback ladder means it is not always the configured one.
    /// </summary>
    public PermissionMode? PermissionModeUsed => Record.PermissionModeUsed;

    /// <inheritdoc cref="PermissionModeUsed"/>
    public string PermissionModeUsedText =>
        Record.PermissionModeUsed is { } mode ? mode.ToCliValue() : "—";

    public int? ExitCode => Record.ExitCode;

    /// <summary>Where the full transcript lives on disk.</summary>
    public string TranscriptPath { get; }

    public bool HasTranscript => File.Exists(TranscriptPath);

    /// <summary>Set when the run stopped mid-task and its session can be continued (plan.md §6.1).</summary>
    public bool IsResumable => Record.IsResumable;

    static string Percent(double value) =>
        value.ToString("0", CultureInfo.InvariantCulture) + "%";
}

/// <summary>
/// One outcome filter chip (plan.md §9.3). Selecting none means "show everything", which is what a
/// user expects from a strip of chips that all start off.
/// </summary>
public sealed partial class OutcomeFilterViewModel : ViewModelBase
{
    readonly Action _onChanged;

    public OutcomeFilterViewModel(RunOutcome outcome, Action onChanged)
    {
        Outcome = outcome;
        Label = DashboardViewModel.DescribeOutcome(outcome);
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
    }

    public RunOutcome Outcome { get; }

    /// <summary>Chip caption, e.g. <c>Rate limited</c>.</summary>
    public string Label { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>How many runs in the loaded history carry this outcome.</summary>
    [ObservableProperty]
    public partial int Count { get; internal set; }

    [RelayCommand]
    void Toggle() => IsSelected = !IsSelected;

    partial void OnIsSelectedChanged(bool value) => _onChanged();
}
