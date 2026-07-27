using Avalonia.Controls;
using Avalonia.Interactivity;
using NightShift.Desktop.ViewModels;
using NightShift.Desktop.Services;

namespace NightShift.Desktop.Views;

/// <summary>
/// The plan window's contents. Almost declarative: the only thing here is wiring a link click to
/// the shell, which the renderer cannot do for itself because it has no services.
/// </summary>
public partial class PlanDocumentView : UserControl
{
    /// <summary>
    /// Set by the presenter before the view is shown, so a link opens in the browser.
    /// </summary>
    /// <remarks>
    /// A property rather than constructor injection because <see cref="ViewLocator"/> builds views
    /// with <c>Activator.CreateInstance</c> and cannot pass anything.
    /// </remarks>
    public IShellLauncher? ShellLauncher { get; set; }

    public PlanDocumentView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        Rendered.OpenLink = url => ShellLauncher?.OpenUrl(url);

        if (DataContext is PlanDocumentViewModel viewModel)
        {
            _ = viewModel.LoadAsync();
        }
    }
}
