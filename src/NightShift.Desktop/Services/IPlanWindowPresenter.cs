using Microsoft.Extensions.Logging;

namespace NightShift.Desktop.Services;

/// <summary>
/// Opens and closes the plan window.
/// </summary>
/// <remarks>
/// A view model must not hold a <c>Window</c>. This is also why the dashboard does not gain window
/// plumbing: its constructor already takes sixteen dependencies, and it only needs to be able to
/// say "show the plan", not to know what showing it involves.
/// </remarks>
public interface IPlanWindowPresenter
{
    /// <summary>Shows the plan window, focusing the existing one when it is already open.</summary>
    void Show();

    /// <summary>
    /// Hides the window. Called when the main window goes to the tray, so the plan window is not
    /// left on screen with no visible parent.
    /// </summary>
    void Hide();
}

/// <summary>
/// The placeholder registered before a window exists: showing does nothing, and says so.
/// </summary>
/// <remarks>
/// Fails quiet rather than closed. Unlike a confirmation, nothing unsafe follows from a window that
/// did not open — but a silent no-op with no log line is how a mis-wired build looks like a bug in
/// the button.
/// </remarks>
public sealed class UnavailablePlanWindowPresenter : IPlanWindowPresenter
{
    readonly ILogger<UnavailablePlanWindowPresenter> _logger;

    public UnavailablePlanWindowPresenter(ILogger<UnavailablePlanWindowPresenter> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public void Show() =>
        _logger.LogWarning("No plan window is available in this host, so the plan cannot be shown.");

    public void Hide()
    {
    }
}
