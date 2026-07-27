namespace NightShift.Desktop.Services;

/// <summary>
/// Single-instance enforcement via a named <see cref="Mutex"/> (plan.md §9.4).
/// </summary>
/// <remarks>
/// <para>
/// Two NightShift processes would mean two schedulers, two run gates, and two writers racing over
/// <c>settings.json</c>, <c>state.json</c> and <c>runs/index.jsonl</c>. The mutex is held for the
/// lifetime of the object, so ownership is released when the app exits — including on a crash,
/// where the OS abandons it and the next launch acquires it cleanly.
/// </para>
/// <para>
/// The name is session-local (no <c>Global\</c> prefix): two different Windows users are entitled
/// to run their own pilot against their own <c>%APPDATA%</c>.
/// </para>
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>The mutex name used unless one is supplied.</summary>
    public const string DefaultMutexName = "NightShift.SingleInstance.6d1f1b6e";

    readonly Mutex? _mutex;
    readonly bool _owned;
    bool _disposed;

    /// <param name="mutexName">Overridable so tests can use a unique name per case.</param>
    public SingleInstanceGuard(string? mutexName = null)
    {
        MutexName = string.IsNullOrWhiteSpace(mutexName) ? DefaultMutexName : mutexName;

        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            IsFirstInstance = createdNew;
            _owned = createdNew;
        }
        catch (AbandonedMutexException)
        {
            // The previous owner died without releasing. Ownership transfers to us, which is exactly
            // what we want: the app that held it is gone.
            IsFirstInstance = true;
            _owned = true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            // A locked-down or unusual environment must not brick the app. Fail open: a second
            // instance is a nuisance, refusing to start at all is worse.
            IsFirstInstance = true;
            _owned = false;
            AcquisitionError = ex.Message;
        }
    }

    /// <summary>The name this guard used.</summary>
    public string MutexName { get; }

    /// <summary>
    /// False when another NightShift already holds the mutex. The caller should surface the running
    /// window and exit rather than continue starting up.
    /// </summary>
    public bool IsFirstInstance { get; }

    /// <summary>
    /// Why the mutex could not be created, when it could not. Non-null means
    /// <see cref="IsFirstInstance"/> is a fail-open guess, not a measurement.
    /// </summary>
    public string? AcquisitionError { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_mutex is null)
        {
            return;
        }

        try
        {
            if (_owned)
            {
                _mutex.ReleaseMutex();
            }
        }
        catch (Exception ex) when (ex is ApplicationException or ObjectDisposedException)
        {
            // Already released or abandoned; nothing to clean up.
        }

        _mutex.Dispose();
    }
}
