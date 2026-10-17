using System.Globalization;

namespace TCV.App.Localization;

/// <summary>
/// Number-aware wording for <see cref="string.Format(IFormatProvider, string, object[])"/> (6.40), so
/// the dictionaries do not fall back to "file(s)" or "Файлов: 5". A format item
/// <c>{0:plural:file|files}</c> picks a form by the number in argument 0; the number itself is a
/// separate item: <c>"{0} {0:plural:file|files}"</c>. English has two forms (one, other), Russian
/// three (one, few, many: <c>{0:plural:файл|файла|файлов}</c>). Any other format item is formatted
/// as usual.
/// </summary>
/// <remarks>
/// Only <see cref="LocalizationManager.Format"/> understands the forms: plain
/// <see cref="string.Format(string, object[])"/> would print the format text as a custom number
/// format. <c>LocalizationGuardTests</c> keeps such keys away from plain formatting.
/// </remarks>
public sealed class PluralFormat : IFormatProvider, ICustomFormatter
{
    public const string Prefix = "plural:";

    private readonly string _language;
    private readonly CultureInfo _culture;

    public PluralFormat(string language, CultureInfo culture)
    {
        _language = language;
        _culture = culture;
    }

    /// <summary>Formats <paramref name="format"/> with the plural rules of <paramref name="language"/>.</summary>
    public static string Format(string language, string format, params object?[] args) =>
        string.Format(new PluralFormat(language, CultureInfo.CurrentCulture), format, args);

    public object? GetFormat(Type? formatType) =>
        formatType == typeof(ICustomFormatter) ? this : _culture.GetFormat(formatType);

    public string Format(string? format, object? arg, IFormatProvider? formatProvider)
    {
        if (format is not null && format.StartsWith(Prefix, StringComparison.Ordinal))
        {
            var forms = format[Prefix.Length..].Split('|');
            return forms[Math.Min(FormIndex(_language, ToCount(arg)), forms.Length - 1)];
        }

        return arg is IFormattable formattable
            ? formattable.ToString(format, _culture)
            : arg?.ToString() ?? string.Empty;
    }

    /// <summary>How many forms a language's strings carry: three for the Slavic languages (one, few,
    /// many), two for the rest (one, other). <c>LocalizationGuardTests</c> counts them.</summary>
    public static int FormCount(string language) => language is "ru" or "uk" or "pl" ? 3 : 2;

    /// <summary>
    /// Which form a count takes: 0 — one, 1 — few (Slavic) or other, 2 — many (Slavic). Russian and
    /// Ukrainian follow the last two digits: 1, 21, 101 → one; 2–4, 22–24 → few; 0, 5–20, 25 → many.
    /// Polish has "one" for 1 alone (21 is many), few for 2–4, 22–24, many for the rest. French counts
    /// 0 as singular ("0 fichier"); the other languages put 0 with the plural.
    /// </summary>
    public static int FormIndex(string language, long count)
    {
        var n = Math.Abs(count);
        var lastTwo = n % 100;
        var last = n % 10;

        switch (language)
        {
            case "ru" or "uk":
                if (last == 1 && lastTwo != 11)
                {
                    return 0;
                }

                return last is >= 2 and <= 4 && lastTwo is < 12 or > 14 ? 1 : 2;
            case "pl":
                if (n == 1)
                {
                    return 0;
                }

                return last is >= 2 and <= 4 && lastTwo is < 12 or > 14 ? 1 : 2;
            case "fr":
                return n <= 1 ? 0 : 1;
            default:
                return n == 1 ? 0 : 1;
        }
    }

    private static long ToCount(object? arg) => arg switch
    {
        null => 0,
        long l => l,
        int i => i,
        IConvertible convertible => convertible.ToInt64(CultureInfo.InvariantCulture),
        _ => 0,
    };
}
