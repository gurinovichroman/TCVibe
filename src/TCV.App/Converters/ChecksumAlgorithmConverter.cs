using System.Globalization;
using System.Windows.Data;
using TCV.Tools;

namespace TCV.App.Converters;

/// <summary>
/// Converts between <see cref="ChecksumAlgorithm"/> and <see cref="bool"/> for RadioButton binding.
/// ConverterParameter should be the enum member name, e.g. "MD5", "SHA1", "SHA256".
/// </summary>
[ValueConversion(typeof(ChecksumAlgorithm), typeof(bool))]
public sealed class ChecksumAlgorithmConverter : IValueConverter
{
    public static readonly ChecksumAlgorithmConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ChecksumAlgorithm algorithm && parameter is string paramStr)
            return algorithm.ToString().Equals(paramStr, StringComparison.OrdinalIgnoreCase);
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true && parameter is string paramStr &&
            Enum.TryParse<ChecksumAlgorithm>(paramStr, ignoreCase: true, out var result))
            return result;
        return Binding.DoNothing;
    }
}
