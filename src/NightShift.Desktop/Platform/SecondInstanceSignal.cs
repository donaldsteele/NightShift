using System.Runtime.Versioning;
using Serilog;

namespace NightShift.Desktop.Platform;

/// <summary>
/// The other half of single-instance enforcement (plan.md §9.4): a named auto-reset event that lets
/// a second launch say "surface your window" to the instance already running, instead of starting a
/// second scheduler.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Services.SingleInstanceGuard"/> decides <em>whether</em> this process is the first one.
/// This decides what the loser does about it. Kept separate because the guard is a pure ownership
/// question with no UI in it, while this is a message to a window.
/// </para>
/// <para>
/// Named synchronisation objects do not exist everywhere. Every operation here degrades to a no-op
/// rather than throwing: on a platform without them the worst case is a second window, which is a
/// nuisance, not a corruption.
/// </para>
/// </remarks>
public sealed class SecondInstanceSignal : IDisposable
{
    /// <summary>Event name used unless one is supplied. Session-local, like the guard's mutex.</summary>
    public const string DefaultEventName = "NightShift.SurfaceWindow.6d1f1b6e";

    readonly EventWaitHandle _signal;
    readonly ManualResetEventSlim _stopping = new(false);
    readonly Thread _worker;
    bool _disposed;

    SecondInstanceSignal(EventWaitHandle signal, Action onSignal)
    {
        _signal = signal;

        _worker = new Thread(() => Wait(onSignal))
        {
            IsBackground = true,
            Name = "NightShift second-instance listener",
        };

        _worker.Start();
    }

    /// <summary>
    /// Starts listening for a second launch. Returns null when the platform has no named events —
    /// the caller carries on regardless.
    /// </summary>
    public static SecondInstanceSignal? Listen(Action onSignal, string? eventName = null)
    {
        ArgumentNullException.ThrowIfNull(onSignal);

        var name = string.IsNullOrWhiteSpace(eventName) ? DefaultEventName : eventName;

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var handle = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, name);
            return new SecondInstanceSignal(handle, onSignal);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException
                                       or UnauthorizedAccessException
                                       or IOException
                                       or WaitHandleCannotBeOpenedException)
        {
            Log.Debug(ex, "Could not listen for a second instance on {EventName}.", name);
            return null;
        }
    }

    /// <summary>
    /// Asks the instance already running to show its window. False when there was nothing to signal.
    /// </summary>
    public static bool TrySignalRunningInstance(string? eventName = null)
    {
        var name = string.IsNullOrWhiteSpace(eventName) ? DefaultEventName : eventName;

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return SignalCore(name);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException
                                       or UnauthorizedAccessException
                                       or IOException
                                       or WaitHandleCannotBeOpenedException)
        {
            Log.Debug(ex, "Could not signal the running instance on {EventName}.", name);
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    static bool SignalCore(string name)
    {
        if (!EventWaitHandle.TryOpenExisting(name, out var handle))
        {
            return false;
        }

        using (handle)
        {
            return handle.Set();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping.Set();

        // Nudge the wait so the thread notices the stop request rather than sitting on the handle.
        try
        {
            _signal.Set();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down.
        }

        _worker.Join(TimeSpan.FromSeconds(1));
        _signal.Dispose();
        _stopping.Dispose();
    }

    void Wait(Action onSignal)
    {
        var handles = new WaitHandle[] { _signal, _stopping.WaitHandle };

        while (!_stopping.IsSet)
        {
            int index;
            try
            {
                index = WaitHandle.WaitAny(handles);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or AbandonedMutexException)
            {
                return;
            }

            if (index != 0 || _stopping.IsSet)
            {
                return;
            }

            try
            {
                onSignal();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Handling a second-instance signal failed.");
            }
        }
    }
}
