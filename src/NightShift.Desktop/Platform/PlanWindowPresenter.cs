using Avalonia.Controls;
using NightShift.Desktop.Services;
using NightShift.Desktop.ViewModels;
using NightShift.Desktop.Views;

namespace NightShift.Desktop.Platform;

/// <summary>
/// The real <see cref="IPlanWindowPresenter"/>: one window, created on first use and reused.
/// </summary>
/// <remarks>
/// <para>
/// One window, not one per click. There is exactly one project directory and one plan file, so a
/// second window over the same file would only create a second unsaved buffer to reconcile with
/// the first. Re-invoking shows and focuses what is already there, the way
/// <c>App.ShowMainWindow</c> does.
/// </para>
/// <para>
/// The window is deliberately unowned — see the note in <c>PlanWindow.axaml</c>.
/// </para>
/// </remarks>
public sealed class PlanWindowPresenter : IPlanWindowPresenter
{
    readonly PlanDocumentViewModel _viewModel;
    readonly IShellLauncher _shell;
    readonly IUiDispatcher _dispatcher;

    PlanWindow? _window;

    public PlanWindowPresenter(
        PlanDocumentViewModel viewModel,
        IShellLauncher shell,
        IUiDispatcher dispatcher)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public void Show() => _dispatcher.Post(ShowCore);

    public void Hide() => _dispatcher.Post(() => _window?.Hide());

    void ShowCore()
    {
        _window ??= new PlanWindow
        {
            DataContext = _viewModel,
            ShellLauncher = _shell,
        };

        _window.Show();

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    /// <summary>Closes the window for good, at shutdown.</summary>
    /// <remarks>
    /// <c>PlanWindow</c> cancels an ordinary close and hides instead, so a real exit has to close
    /// it programmatically or the process is left with a live window it cannot dismiss.
    /// </remarks>
    public void Close()
    {
        var window = _window;
        _window = null;
        window?.Close();
    }
}
