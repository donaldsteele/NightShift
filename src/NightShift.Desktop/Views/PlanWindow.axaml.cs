using Avalonia.Controls;
using Avalonia.Input;
using NightShift.Desktop.Services;
using NightShift.Desktop.ViewModels;

namespace NightShift.Desktop.Views;

/// <summary>The plan window. A shell around <see cref="PlanDocumentView"/>, plus its keyboard.</summary>
public partial class PlanWindow : Window
{
    public PlanWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    /// <summary>Handed to the contents so a link in a plan opens in the browser.</summary>
    public IShellLauncher? ShellLauncher
    {
        get => Contents.ShellLauncher;
        set => Contents.ShellLauncher = value;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (DataContext is PlanDocumentViewModel viewModel)
        {
            // Ctrl+S is what a person editing a document reaches for without thinking.
            if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                if (viewModel.SaveCommand.CanExecute(null))
                {
                    viewModel.SaveCommand.Execute(null);
                }

                e.Handled = true;
                return;
            }

            // Esc closes, but only when there is nothing to lose. With unsaved edits it hands off
            // to Cancel, which asks first -- an Esc that silently discards work is a data-loss path.
            if (e.Key == Key.Escape)
            {
                if (viewModel.IsDirty)
                {
                    viewModel.CancelEditCommand.Execute(null);
                }
                else
                {
                    Hide();
                }

                e.Handled = true;
                return;
            }
        }

        base.OnKeyDown(e);
    }

    /// <summary>
    /// Closing hides rather than destroys: the view model is a singleton that stays subscribed to
    /// the scheduler and the file watcher, and re-opening should be instant.
    /// </summary>
    void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (e.IsProgrammatic)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }
}
