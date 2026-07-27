namespace NightShift.Desktop.Services;

/// <summary>
/// The <see cref="IUiDispatcher"/> test double contract, shipped rather than duplicated per test:
/// every callback runs inline on the calling thread.
/// </summary>
/// <remarks>
/// This is what makes the view models testable headlessly. A test raises a scheduler event on its
/// own thread and, by the time the raise returns, every observable property has already been
/// updated — no message pump, no <c>Avalonia.Headless</c> harness, no polling.
/// <para>
/// It is also the correct dispatcher for the XAML designer and for any host that has no Avalonia
/// dispatcher running, so it lives in the app project rather than in the test project.
/// </para>
/// </remarks>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    /// <summary>Shared instance; the type holds no state.</summary>
    public static ImmediateUiDispatcher Instance { get; } = new();

    /// <summary>Always true: whatever thread the caller is on is treated as the UI thread.</summary>
    public bool IsOnUiThread => true;

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }

    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
        return Task.CompletedTask;
    }

    public Task<T> InvokeAsync<T>(Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action();
    }
}
