using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using NightShift.Desktop.ViewModels;

namespace NightShift.Desktop.Views;

/// <summary>plan.md §9.1.</summary>
/// <remarks>
/// The only thing here that XAML cannot express: keeping the output pane pinned to the tail while a
/// run streams into it. <c>AutoScrollOutput</c> is two-way, so a user who scrolls up and unticks
/// "Follow output" stops being dragged back down.
/// </remarks>
public partial class DashboardView : UserControl
{
    DashboardViewModel? _viewModel;

    public DashboardView()
    {
        InitializeComponent();
        DetachedFromVisualTree += (_, _) => Detach();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        Detach();

        if (DataContext is DashboardViewModel viewModel)
        {
            _viewModel = viewModel;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            ScrollOutputToEnd();
        }

        base.OnDataContextChanged(e);
    }

    void Detach()
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = null;
        }
    }

    void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DashboardViewModel.OutputText) or nameof(DashboardViewModel.AutoScrollOutput))
        {
            ScrollOutputToEnd();
        }
    }

    void ScrollOutputToEnd()
    {
        if (_viewModel?.AutoScrollOutput != true)
        {
            return;
        }

        // Posted at Background priority so the scroll happens after the text block has re-measured;
        // scrolling now would target the extent the pane had one line ago.
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_viewModel?.AutoScrollOutput == true)
                {
                    OutputScroll.ScrollToEnd();
                }
            },
            DispatcherPriority.Background);
    }
}
