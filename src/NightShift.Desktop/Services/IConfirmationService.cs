namespace NightShift.Desktop.Services;

/// <summary>
/// The modal "are you sure?" behind <c>Force run</c> (plan.md §6.1, §9.1) and the destructive
/// Advanced actions.
/// </summary>
/// <remarks>
/// Force run bypasses the usage gate — the one safety rule that stops the pilot burning a weekly
/// cap — so plan.md requires a confirmation, and a view model must not be able to skip it by
/// accident. Behind an interface so a test can assert both branches without a window.
/// </remarks>
public interface IConfirmationService
{
    /// <summary>
    /// Returns true only if the user explicitly confirmed. An implementation that cannot show a
    /// dialog must return false, never true.
    /// </summary>
    Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmLabel,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The placeholder registered before a window exists: every confirmation is refused.
/// </summary>
/// <remarks>
/// Fails closed on purpose. The alternative — defaulting to "yes" until the view layer supplies a
/// real dialog — would mean a mis-wired build silently force-runs against an exhausted quota.
/// </remarks>
public sealed class DeclineConfirmationService : IConfirmationService
{
    public Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmLabel,
        CancellationToken cancellationToken = default) => Task.FromResult(false);
}
