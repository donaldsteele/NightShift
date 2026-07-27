using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using NightShift.Desktop.ViewModels;

namespace NightShift.Desktop.Views;

/// <summary>plan.md §9.3.</summary>
/// <remarks>
/// The view model finds the matches; this puts the caret on the current one. Selection and caret
/// position are view state that no view model should hold, and "next match" means nothing to the
/// user unless the pane actually scrolls there.
/// </remarks>
public partial class HistoryView : UserControl
{
    HistoryViewModel? _viewModel;

    public HistoryView()
    {
        InitializeComponent();
        DetachedFromVisualTree += (_, _) => Detach();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        Detach();

        if (DataContext is HistoryViewModel viewModel)
        {
            _viewModel = viewModel;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
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
        if (e.PropertyName is nameof(HistoryViewModel.SelectedMatchOffset)
            or nameof(HistoryViewModel.SelectedMatchIndex)
            or nameof(HistoryViewModel.TranscriptText))
        {
            Dispatcher.UIThread.Post(HighlightCurrentMatch, DispatcherPriority.Background);
        }
    }

    void HighlightCurrentMatch()
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        var offset = viewModel.SelectedMatchOffset;
        var length = viewModel.SelectedMatchLength;

        if (offset < 0 || length <= 0 || offset + length > (TranscriptBox.Text?.Length ?? 0))
        {
            TranscriptBox.SelectionStart = 0;
            TranscriptBox.SelectionEnd = 0;
            return;
        }

        // CaretIndex last: setting it is what brings the match into view.
        TranscriptBox.SelectionStart = offset;
        TranscriptBox.SelectionEnd = offset + length;
        TranscriptBox.CaretIndex = offset + length;
    }
}
