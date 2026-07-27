using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Microsoft.Extensions.Logging;
using CoreClipboard = NightShift.Core.Execution.IClipboard;

namespace NightShift.Desktop.Services;

/// <summary>
/// <see cref="NightShift.Core.Execution.IClipboard"/> over Avalonia's platform clipboard, so
/// <see cref="NightShift.Core.Execution.TerminalClaudeRunner"/> can hand the prompt over on a
/// visible-terminal launch (plan.md §5.5) and the dashboard can copy the live output pane.
/// </summary>
/// <remarks>
/// <para>
/// The clipboard hangs off a <see cref="Avalonia.Controls.TopLevel"/> and must be touched from the
/// UI thread, but the runner that calls this is on a background thread, so every call goes through
/// <see cref="IUiDispatcher"/>.
/// </para>
/// <para>
/// Copying is contractually best-effort: <see cref="TrySetTextAsync"/> returns false rather than
/// throwing when there is no window yet, when the platform has no clipboard, or when another
/// process holds it. A failed copy costs the user a paste; an exception escaping into an unattended
/// run costs them the run.
/// </para>
/// </remarks>
public sealed class AvaloniaClipboard : CoreClipboard
{
    readonly IUiDispatcher _dispatcher;
    readonly Func<IClipboard?> _resolve;
    readonly ILogger<AvaloniaClipboard> _logger;

    /// <param name="resolve">
    /// Overrides how the platform clipboard is found. Defaults to the desktop lifetime's main
    /// window; injectable so this class is reachable from a test without a running Avalonia app.
    /// </param>
    public AvaloniaClipboard(
        IUiDispatcher dispatcher,
        ILogger<AvaloniaClipboard> logger,
        Func<IClipboard?>? resolve = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _resolve = resolve ?? ResolveFromApplication;
    }

    public async Task<bool> TrySetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            return await _dispatcher.InvokeAsync(async () =>
            {
                var clipboard = _resolve();
                if (clipboard is null)
                {
                    return false;
                }

                await clipboard.SetTextAsync(text).ConfigureAwait(true);
                return true;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            // Deliberately broad: platform clipboards throw a different type per backend, and none
            // of them is worth failing a run over.
            _logger.LogWarning(ex, "Could not copy to the clipboard.");
            return false;
        }
    }

    static IClipboard? ResolveFromApplication() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow?.Clipboard
            : null;
}
