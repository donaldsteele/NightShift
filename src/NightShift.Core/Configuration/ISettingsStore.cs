namespace NightShift.Core.Configuration;

/// <summary>
/// Reads and writes <see cref="PilotSettings"/>. Implementations must never throw because of a
/// bad file on disk — a corrupt settings file is recovered from, not fatal.
/// </summary>
public interface ISettingsStore
{
    /// <summary>
    /// The most recently loaded or saved settings. Defaults until <see cref="LoadAsync"/> runs.
    /// </summary>
    PilotSettings Current { get; }

    /// <summary>
    /// Raised after <see cref="Current"/> changes, so long-lived consumers (the scheduler) can
    /// react without a restart. Raised on the calling thread.
    /// </summary>
    event EventHandler<PilotSettings>? SettingsChanged;

    Task<PilotSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(PilotSettings settings, CancellationToken cancellationToken = default);
}
