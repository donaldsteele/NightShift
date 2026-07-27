using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using NightShift.Desktop.Services;

namespace NightShift.Desktop.Platform;

/// <summary>
/// The modal behind <c>Force run</c> and the other destructive actions (plan.md §6.1, §9.1).
/// </summary>
public sealed partial class ConfirmationDialog : Window
{
    /// <summary>Parameterless constructor for the XAML loader and the designer.</summary>
    public ConfirmationDialog()
        : this("Confirm", string.Empty, "Continue")
    {
    }

    public ConfirmationDialog(string title, string message, string confirmLabel)
    {
        InitializeComponent();

        Title = title;
        HeadingText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmLabel;

        ConfirmButton.Click += OnConfirm;
        CancelButton.Click += OnCancel;
    }

    void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

    void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}

/// <summary>
/// The real <see cref="IConfirmationService"/>: a modal <see cref="ConfirmationDialog"/> owned by
/// the main window.
/// </summary>
/// <remarks>
/// Fails closed, exactly like the placeholder it replaces. No window, a cancelled token, or a
/// dialog that could not be shown all answer "no" — never "yes". <c>Force run</c> is the one action
/// that can deliberately burn a weekly cap, so an unanswerable question must never count as consent.
/// </remarks>
public sealed class DialogConfirmationService : IConfirmationService
{
    public Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmLabel,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(false);
        }

        return UiThread.RunAsync(async () =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
            {
                return false;
            }

            // A dialog behind a hidden window would be invisible and unanswerable: the app would
            // look hung. Surface the owner first (the tray "Run now" path can get here with the
            // window minimized to tray).
            if (!owner.IsVisible)
            {
                owner.Show();
            }

            var dialog = new ConfirmationDialog(title, message, confirmLabel);
            return await dialog.ShowDialog<bool>(owner).ConfigureAwait(true);
        });
    }
}
