using System.Globalization;
using System.Windows;
using System.Windows.Data;
using TCV.App.Settings;

namespace TCV.App.Converters;

/// <summary>
/// Returns Visible when the bound ViewMode matches the ConverterParameter string.
/// Usage: Visibility="{Binding ViewMode, Converter={StaticResource ViewModeVis}, ConverterParameter=Details}"
/// </summary>
[ValueConversion(typeof(ViewMode), typeof(Visibility))]
public sealed class ViewModeToVisibilityConverter : IValueConverter
{
    public static readonly ViewModeToVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ViewMode mode && parameter is string param &&
            Enum.TryParse<ViewMode>(param, out var target))
            return mode == target ? Visibility.Visible : Visibility.Collapsed;

        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
