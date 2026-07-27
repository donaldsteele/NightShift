using Avalonia.Threading;

namespace NightShift.Desktop.Services;

/// <summary>The real <see cref="IUiDispatcher"/>: Avalonia's <see cref="Dispatcher.UIThread"/>.</summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public bool IsOnUiThread => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Dispatcher.UIThread.Post(action);
    }

    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }

    public Task<T> InvokeAsync<T>(Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        return Dispatcher.UIThread.CheckAccess()
            ? action()
            : Dispatcher.UIThread.InvokeAsync(action);
    }
}
