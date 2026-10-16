using System.Globalization;
using System.Windows.Data;
using TCV.App.Localization;

namespace TCV.App.Converters;

/// <summary>
/// Shows an enum member as text from the string dictionary: the key is the converter parameter
/// (a prefix) followed by the member name, so <c>Transfer_Status_</c> + <c>Paused</c> reads
/// <c>Transfer_Status_Paused</c>. A missing key renders as <c>[key]</c>, which is how a forgotten
/// translation announces itself.
/// </summary>
[ValueConversion(typeof(Enum), typeof(string))]
public sealed class EnumLocalizationConverter : IValueConverter
{
    public static readonly EnumLocalizationConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null ? string.Empty : LocalizationManager.Get((parameter as string ?? string.Empty) + value);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
