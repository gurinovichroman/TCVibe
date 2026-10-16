using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TCV.App.Converters;

/// <summary>
/// A solid brush → the same colour at the opacity given as the parameter («0.45»): the design's
/// <c>color-mix(var(--workspace) 45%, transparent)</c> for the cursor's hairline.
/// </summary>
public sealed class BrushOpacityConverter : IValueConverter
{
    public static readonly BrushOpacityConverter Instance = new();

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not SolidColorBrush brush)
        {
            return value;
        }

        var opacity = double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var o) ? o : 1.0;
        var color = brush.Color;
        var result = new SolidColorBrush(Color.FromArgb((byte)Math.Round(color.A * Math.Clamp(opacity, 0, 1)), color.R, color.G, color.B));
        result.Freeze();
        return result;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
