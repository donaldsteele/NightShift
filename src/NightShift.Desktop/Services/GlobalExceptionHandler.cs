using Avalonia.Threading;
using Serilog;

namespace NightShift.Desktop.Services;

/// <summary>
/// Catches the exceptions that would otherwise kill the process silently (plan.md §10, Phase 6).
/// </summary>
/// <remarks>
/// <para>
/// An unattended pilot fails at 3am with nobody watching, so "the app vanished and left no note" is
/// the worst possible outcome. Every handler here logs first and asks questions later.
/// </para>
/// <para>
/// The three routes are genuinely different: <see cref="AppDomain.UnhandledException"/> is terminal
/// and only gives us a chance to write the log; <see cref="TaskScheduler.UnobservedTaskException"/>
/// is recoverable and is marked observed so a stray fire-and-forget cannot take the process down;
/// and the Avalonia dispatcher's handler catches everything thrown on the UI thread, which is where
/// a bad binding or a command handler lands.
/// </para>
/// </remarks>
public static class GlobalExceptionHandler
{
    static int _installed;

    /// <summary>
    /// Raised for an exception that was survived rather than fatal, so the UI can surface a
    /// non-fatal notice. Never raised on the terminal path — there is nothing left to show it with.
    /// </summary>
    public static event Action<Exception>? NonFatalError;

    /// <summary>Installs the handlers. Safe to call more than once; only the first call takes effect.</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            // Terminal: the runtime is on its way down. Flush, because the sink buffers.
            if (e.ExceptionObject is Exception ex)
            {
                Log.Fatal(ex, "Unhandled exception; the process is terminating (IsTerminating={IsTerminating}).", e.IsTerminating);
            }
            else
            {
                Log.Fatal("Unhandled non-exception throw: {Object}. Terminating={IsTerminating}.", e.ExceptionObject, e.IsTerminating);
            }

            Log.CloseAndFlush();
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // Recoverable by construction: marking it observed stops the finalizer escalating it.
            Log.Error(e.Exception, "Unobserved task exception; continuing.");
            e.SetObserved();
            Raise(e.Exception);
        };

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log.Error(e.Exception, "Unhandled exception on the UI thread; continuing.");

            // Keep the window alive. A crashed dispatcher would leave a tray icon attached to a dead
            // app, which is worse than a degraded one — the scheduler is still running underneath.
            e.Handled = true;
            Raise(e.Exception);
        };
    }

    static void Raise(Exception exception)
    {
        try
        {
            NonFatalError?.Invoke(exception);
        }
        catch (Exception ex)
        {
            // A throwing error handler must not become the next unhandled exception.
            Log.Error(ex, "The non-fatal error handler itself threw.");
        }
    }
}
