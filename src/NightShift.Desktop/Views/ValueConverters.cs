using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using NightShift.Core.Configuration;
using NightShift.Core.Usage;
using NightShift.Desktop.ViewModels;

namespace NightShift.Desktop.Views;

/// <summary>
/// True when the bound value's name equals the converter parameter. Used for the class bindings that
/// colour the status pill by <see cref="PilotStatusKind"/>, where a style selector cannot compare an
/// enum on its own.
/// </summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null &&
        parameter is not null &&
        string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}

/// <summary>
/// Renders a settings enum with the wording <see cref="SettingsViewModel"/> already owns, so a
/// combo box never invents its own labels and drift between the two becomes impossible.
/// </summary>
public sealed class SettingsLabelConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        UsageMetric metric => SettingsViewModel.Describe(metric),
        UsageUnavailableBehavior behavior => SettingsViewModel.Describe(behavior),
        LaunchMode mode => SettingsViewModel.Describe(mode),
        ResumeStrategy strategy => SettingsViewModel.Describe(strategy),
        PermissionMode mode => SettingsViewModel.Describe(mode),
        CavemanLevel level => SettingsViewModel.Describe(level),
        UsageProviderPreference preference => SettingsViewModel.Describe(preference),
        _ => value?.ToString(),
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}
