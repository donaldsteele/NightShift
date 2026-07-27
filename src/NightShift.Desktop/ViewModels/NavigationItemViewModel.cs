namespace NightShift.Desktop.ViewModels;

/// <summary>The three destinations in the left rail (plan.md §9).</summary>
public enum NavigationSection
{
    /// <summary>plan.md §9.1. The default view.</summary>
    Dashboard,

    /// <summary>plan.md §9.3.</summary>
    History,

    /// <summary>plan.md §9.2.</summary>
    Settings,
}

/// <summary>
/// One row of the <c>NavigationView</c>-style left rail. The rail is a fixed list of three, so this
/// is a plain immutable record rather than an observable object; selection lives on
/// <see cref="MainWindowViewModel.CurrentSection"/>.
/// </summary>
/// <param name="Section">Which destination this row selects.</param>
/// <param name="Title">Rail label.</param>
/// <param name="Description">Tooltip / secondary line.</param>
public sealed record NavigationItemViewModel(
    NavigationSection Section,
    string Title,
    string Description);
