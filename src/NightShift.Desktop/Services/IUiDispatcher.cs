namespace NightShift.Desktop.Services;

/// <summary>
/// The one place the view models are allowed to know about a UI thread.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="NightShift.Core.Scheduling.PilotScheduler.CycleCompleted"/> and
/// <see cref="NightShift.Core.Scheduling.PilotScheduler.RunProgress"/> are raised from the
/// background service's own thread, and <see cref="NightShift.Core.Configuration.ISettingsStore"/>
/// documents that it raises on the calling thread. Every one of those handlers mutates
/// <c>[ObservableProperty]</c> state and <see cref="System.Collections.ObjectModel.ObservableCollection{T}"/>
/// instances that Avalonia bindings are attached to, so each has to be marshalled.
/// </para>
/// <para>
/// It is an interface rather than a direct <c>Dispatcher.UIThread</c> call so the view models can be
/// exercised headlessly: a test substitutes <see cref="ImmediateUiDispatcher"/>, which runs the
/// callback inline, and every assertion afterwards observes the finished state with no pumping and
/// no <c>Avalonia.Headless</c> dependency.
/// </para>
/// </remarks>
public interface IUiDispatcher
{
    /// <summary>True when the caller is already on the UI thread and may mutate bound state directly.</summary>
    bool IsOnUiThread { get; }

    /// <summary>
    /// Queues <paramref name="action"/> for the UI thread and returns immediately. The right choice
    /// for a stream event: a run emits thousands of them and none of them is worth blocking a
    /// background thread on.
    /// </summary>
    void Post(Action action);

    /// <summary>Runs <paramref name="action"/> on the UI thread and completes when it has finished.</summary>
    Task InvokeAsync(Action action);

    /// <summary>
    /// Runs an asynchronous <paramref name="action"/> on the UI thread and yields its result. Needed
    /// for the platform APIs that must be touched from the UI thread and are themselves async — the
    /// clipboard, above all.
    /// </summary>
    Task<T> InvokeAsync<T>(Func<Task<T>> action);
}
