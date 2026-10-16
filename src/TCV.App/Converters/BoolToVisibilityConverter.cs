using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TCV.App.Converters;

/// <summary>
/// Visible when the bound value is true. Parameter <c>inverse</c> flips it; <c>notEmpty</c> makes a
/// non-empty string count as true (the quick-search bar binds the typed text directly). Parameters
/// are matched ignoring case: a mistyped "Inverse" used to leave a button hidden for good.
/// </summary>
[ValueConversion(typeof(object), typeof(Visibility))]
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public static readonly BoolToVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isTrue = value switch
        {
            bool b => b,
            string s => s.Length > 0,
            null => false,
            _ => true,
        };

        if (parameter is string p && string.Equals(p, "inverse", StringComparison.OrdinalIgnoreCase))
            isTrue = !isTrue;

        return isTrue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}
