using System.Globalization;
using System.Windows.Data;

namespace TCV.App.Converters;

[ValueConversion(typeof(long), typeof(string))]
public sealed class FileSizeConverter : IValueConverter
{
    public static readonly FileSizeConverter Instance = new();

    // The unit names follow the interface language («1,2 КБ»); LocalizationManager sets them on every
    // switch. English until then, which is also what headless tests see.
    private static string[] _units = ["B", "KB", "MB", "GB"];

    // …and so does the decimal mark («1,2» in Russian): the interface's culture, not the machine's.
    private static CultureInfo? _culture;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is long size ? Format(size) : string.Empty;

    /// <summary>Unit names for bytes, kilo-, mega- and gigabytes, and the number format, of the interface language.</summary>
    public static void UseUnits(string bytes, string kilo, string mega, string giga, CultureInfo? culture = null)
    {
        _units = [bytes, kilo, mega, giga];
        _culture = culture;
    }

    /// <summary>
    /// Human-readable size. Zero is a real size ("0 B" for an empty file), never a folder marker:
    /// whether a row is a folder is the row's business, not the number's.
    /// </summary>
    public static string Format(long size)
    {
        var units = _units;
        var culture = _culture ?? CultureInfo.CurrentCulture;
        return size switch
        {
            < 0 => string.Empty,
            < 1024 => string.Format(culture, "{0:N0} {1}", size, units[0]),
            < 1024 * 1024 => string.Format(culture, "{0:N1} {1}", size / 1024.0, units[1]),
            < 1024 * 1024 * 1024 => string.Format(culture, "{0:N1} {1}", size / (1024.0 * 1024), units[2]),
            _ => string.Format(culture, "{0:N2} {1}", size / (1024.0 * 1024 * 1024), units[3]),
        };
    }

    /// <summary>A disk's capacity at a glance: whole gigabytes from 10 GB up («182 ГБ»), <see cref="Format"/> below.</summary>
    public static string FormatCompact(long size) =>
        size >= 10L * 1024 * 1024 * 1024
            ? string.Format(_culture ?? CultureInfo.CurrentCulture, "{0:N0} {1}", size / (1024.0 * 1024 * 1024), _units[3])
            : Format(size);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
