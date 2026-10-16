using System.Globalization;
using System.Windows.Data;

namespace TCV.App.Converters;

/// <summary>
/// Returns true when the bound enum value matches the ConverterParameter string.
/// One-way: use Click handlers to change the value.
/// </summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public static readonly EnumToBoolConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (parameter is string param && value is Enum enumVal)
            return enumVal.ToString() == param;
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true && parameter is string param && targetType.IsEnum)
            return Enum.Parse(targetType, param);
        return Binding.DoNothing;
    }
}
