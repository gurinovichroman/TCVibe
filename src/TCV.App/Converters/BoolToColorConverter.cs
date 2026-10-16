using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace TCV.App.Converters;

/// <summary>
/// Converts a bool to a status Brush from the current theme: true (error) is the theme's danger
/// colour, false (ok) its success colour. Read on every conversion, so a theme switch shows through.
/// </summary>
[ValueConversion(typeof(bool), typeof(Brush))]
public sealed class BoolToColorConverter : IValueConverter
{
    public static readonly BoolToColorConverter Instance = new();

    // Only used when no theme is loaded (tests, design time).
    private static readonly Brush FallbackError = new SolidColorBrush(Color.FromRgb(0xF4, 0x87, 0x71));
    private static readonly Brush FallbackOk = new SolidColorBrush(Color.FromRgb(0x57, 0xA6, 0x4A));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isError = value is true;
        var themed = Application.Current?.TryFindResource(isError ? "DangerBrush" : "SuccessBrush") as Brush;
        return themed ?? (isError ? FallbackError : FallbackOk);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
