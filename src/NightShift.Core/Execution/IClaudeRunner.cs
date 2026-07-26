using NightShift.Core.Configuration;
using NightShift.Core.History;

namespace NightShift.Core.Execution;

/// <summary>
/// Everything a caller wants to know while a run is in flight. The dashboard subscribes to this;
/// the scheduler ignores it.
/// </summary>
/// <param name="RunId">Correlates with <see cref="RunRecord.Id"/> and the transcript file name.</param>
public sealed record RunProgress(string RunId, ClaudeStreamEvent Event);

/// <summary>
/// Launches Claude Code against the project directory and reports what happened (plan.md §5).
/// Implementations must return a <see cref="RunRecord"/> for every outcome — a failure to launch is
/// a recorded run, not an exception, because an unattended pilot has nobody to show a stack trace to.
/// </summary>
public interface IClaudeRunner
{
    /// <summary>Which launch mode this runner implements.</summary>
    LaunchMode Mode { get; }

    /// <summary>
    /// Runs one cycle. <paramref name="progress"/> receives stream events as they arrive; it is null
    /// for headless callers that only want the final record, and always null in terminal mode where
    /// output cannot be captured.
    /// </summary>
    Task<RunRecord> RunAsync(
        PilotSettings settings,
        IProgress<RunProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
