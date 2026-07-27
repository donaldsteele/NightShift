using NightShift.Core.Configuration;
using NightShift.Core.Execution;
using NightShift.Core.History;
using NightShift.Core.Preflight;
using NightShift.Core.Usage;
using NightShift.Desktop.Services;

namespace NightShift.Desktop.Tests;

/// <summary>In-memory <see cref="ISettingsStore"/> that records every save.</summary>
sealed class FakeSettingsStore : ISettingsStore
{
    public FakeSettingsStore(PilotSettings? initial = null)
    {
        Current = (initial ?? new PilotSettings()).Normalized();
    }

    public PilotSettings Current { get; private set; }

    public List<PilotSettings> Saves { get; } = [];

    public int SaveCount => Saves.Count;

    public event EventHandler<PilotSettings>? SettingsChanged;

    public Task<PilotSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Current);

    public Task SaveAsync(PilotSettings settings, CancellationToken cancellationToken = default)
    {
        Current = settings.Normalized();
        Saves.Add(Current);
        SettingsChanged?.Invoke(this, Current);
        return Task.CompletedTask;
    }
}

/// <summary>A usage provider whose answer the test sets.</summary>
sealed class FakeUsageProvider : IUsageProvider
{
    public FakeUsageProvider(UsageSource source = UsageSource.OAuth)
    {
        Source = source;
        Snapshot = UsageSnapshot.Unavailable("no fake snapshot configured", DateTimeOffset.UnixEpoch);
    }

    public UsageSource Source { get; }

    public UsageSnapshot Snapshot { get; set; }

    public Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Snapshot);
}

/// <summary>A preflight checker whose result the test sets.</summary>
sealed class FakePreflightChecker : IPreflightChecker
{
    public PreflightResult Result { get; set; } =
        new([], DateTimeOffset.UnixEpoch, PlanItemCounts.Empty);

    public int CheckCount { get; private set; }

    public Task<PreflightResult> CheckAsync(
        PilotSettings settings,
        CancellationToken cancellationToken = default)
    {
        CheckCount++;
        return Task.FromResult(Result);
    }
}

/// <summary>Records which fixes were handed to Core rather than to the app.</summary>
sealed class FakePreflightFixApplier : IPreflightFixApplier
{
    public List<PreflightFixAction> Applied { get; } = [];

    public PreflightFixOutcome Outcome { get; set; } = PreflightFixOutcome.Applied;

    public Task<PreflightFixResult> ApplyAsync(
        PreflightFixAction fix,
        PilotSettings settings,
        CancellationToken cancellationToken = default)
    {
        Applied.Add(fix);
        return Task.FromResult(new PreflightFixResult(fix.Command, Outcome, $"fake: {fix.Command}"));
    }
}

/// <summary>
/// A runner that returns a canned record, optionally blocking until the test lets it finish so the
/// "a run is in flight" states can be observed.
/// </summary>
sealed class FakeClaudeRunner : IClaudeRunner
{
    public FakeClaudeRunner(LaunchMode mode = LaunchMode.Headless)
    {
        Mode = mode;
    }

    public LaunchMode Mode { get; }

    public RunRecord Record { get; set; } = RunRecord.Start(DateTimeOffset.UnixEpoch) with
    {
        EndedAt = DateTimeOffset.UnixEpoch,
        Outcome = RunOutcome.Success,
    };

    /// <summary>Set to hold the run open; complete it to let <c>RunAsync</c> return.</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>Completed once <c>RunAsync</c> has been entered.</summary>
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Events pushed to <c>progress</c> before the record is returned.</summary>
    public List<ClaudeStreamEvent> Events { get; } = [];

    public async Task<RunRecord> RunAsync(
        PilotSettings settings,
        IProgress<RunProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Entered.TrySetResult();

        foreach (var streamEvent in Events)
        {
            progress?.Report(new RunProgress(Record.Id, streamEvent));
        }

        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return Record;
    }
}

/// <summary>Records shell requests instead of launching anything.</summary>
sealed class FakeShellLauncher : IShellLauncher
{
    public List<string> Opened { get; } = [];

    public List<string> Revealed { get; } = [];

    public List<string> Urls { get; } = [];

    public bool Result { get; set; } = true;

    public bool OpenPath(string path)
    {
        Opened.Add(path);
        return Result;
    }

    public bool RevealInFileManager(string path)
    {
        Revealed.Add(path);
        return Result;
    }

    public bool OpenUrl(string url)
    {
        Urls.Add(url);
        return Result;
    }
}

/// <summary>Captures what would have gone to the clipboard.</summary>
sealed class FakeClipboard : IClipboard
{
    public List<string> Texts { get; } = [];

    public bool Result { get; set; } = true;

    public Task<bool> TrySetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        Texts.Add(text);
        return Task.FromResult(Result);
    }
}

/// <summary>A confirmation dialog whose answer the test sets.</summary>
sealed class FakeConfirmationService : IConfirmationService
{
    public bool Answer { get; set; }

    public int CallCount { get; private set; }

    public Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmLabel,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.FromResult(Answer);
    }
}

/// <summary>A picker whose answer the test sets.</summary>
sealed class FakePathPicker : IFolderPicker, IFilePicker
{
    public string? FolderResult { get; set; }

    public string? FileResult { get; set; }

    public int FolderCallCount { get; private set; }

    public int FileCallCount { get; private set; }

    public Task<string?> PickFolderAsync(
        string title,
        string? startIn = null,
        CancellationToken cancellationToken = default)
    {
        FolderCallCount++;
        return Task.FromResult(FolderResult);
    }

    public Task<string?> PickFileAsync(
        string title,
        string? startIn = null,
        IReadOnlyList<string>? patterns = null,
        CancellationToken cancellationToken = default)
    {
        FileCallCount++;
        return Task.FromResult(FileResult);
    }
}

/// <summary>Locator that reports whatever the test wants found.</summary>
sealed class FakeExecutableLocator : IClaudeExecutableLocator
{
    public ClaudeExecutableResolution Resolution { get; set; } =
        new(null, ClaudeExecutableSource.NotFound, "not found", []);

    public ClaudeExecutableResolution Locate(PilotSettings settings) => Resolution;

    public ClaudeExecutableResolution Locate(string? configuredPath) => Resolution;
}

/// <summary>Auto-mode probe process that never runs a process.</summary>
sealed class FakeAutoModeProbeRunner : IAutoModeProbeRunner
{
    public AutoModeProbeOutput Output { get; set; } = new(0, "{}", string.Empty);

    public Task<AutoModeProbeOutput> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken) => Task.FromResult(Output);
}

/// <summary>In-memory <c>autoMode.environment</c> editor.</summary>
sealed class FakeAutoModeEnvironmentStore : IAutoModeEnvironmentStore
{
    public string SettingsPath => Path.Combine("fake", "settings.json");

    public List<string> Entries { get; set; } = [];

    public List<IReadOnlyList<string>> Writes { get; } = [];

    public Task<AutoModeEnvironment> ReadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AutoModeEnvironment(Entries));

    public Task<string?> WriteAsync(
        IReadOnlyList<string> entries,
        CancellationToken cancellationToken = default)
    {
        Writes.Add(entries);
        Entries = [.. entries];
        return Task.FromResult<string?>(null);
    }
}

/// <summary>History editor that just records which runs were asked to be deleted.</summary>
sealed class FakeRunHistoryEditor : IRunHistoryEditor
{
    public List<string> Deleted { get; } = [];

    public bool Result { get; set; } = true;

    public Task<bool> DeleteAsync(string runId, CancellationToken cancellationToken = default)
    {
        Deleted.Add(runId);
        return Task.FromResult(Result);
    }
}
