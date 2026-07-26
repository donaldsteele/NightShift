namespace NightShift.Core.Scheduling;

/// <summary>
/// Lets exactly one run be in flight at a time (plan.md §6, §11.5).
/// </summary>
/// <remarks>
/// Deliberately try-and-give-up rather than wait-your-turn. A run that outlasts its interval must
/// make the next tick <b>skip</b>, not queue behind it — queued runs would pile up during a long
/// job and then stampede. The same gate also guards writes to the user's global
/// <c>~/.claude.json</c>, which must never happen while a `claude` process is live (§5.3.1).
/// </remarks>
public sealed class RunGate
{
    readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>True while a run holds the gate.</summary>
    public bool IsRunning => _semaphore.CurrentCount == 0;

    /// <summary>
    /// Takes the gate if it is free. Returns null when a run is already in flight — the caller then
    /// records <c>Skipped(AlreadyRunning)</c> and waits for the next tick.
    /// </summary>
    public IDisposable? TryEnter() => _semaphore.Wait(0) ? new Releaser(_semaphore) : null;

    sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
