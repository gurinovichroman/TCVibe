using System.Globalization;
using System.Windows.Data;

namespace TCV.App.Converters;

[ValueConversion(typeof(DateTime), typeof(string))]
public sealed class DateFormatConverter : IValueConverter
{
    public static readonly DateFormatConverter Instance = new();

    public const string DefaultPattern = "yyyy-MM-dd HH:mm";

    // The list's date pattern is the language's (Format_ListDate: «12.10.2026  10:41» in Russian);
    // LocalizationManager sets it on every switch.
    private static string _pattern = DefaultPattern;

    public static void UsePattern(string pattern) =>
        _pattern = string.IsNullOrWhiteSpace(pattern) || pattern.StartsWith('[') ? DefaultPattern : pattern;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // default(DateTime) means "no date" (the ".." row, a VFS that reports none), not year 1.
        if (value is not DateTime dt || dt == default) return string.Empty;
        return dt.ToString(_pattern, culture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
