using System.Globalization;

namespace TCV.App.ViewModels;

/// <summary>
/// Parses a day typed in the search form's "Modified" fields — ISO <c>2026-10-01</c>, the
/// <c>01.10.2026</c> form the result grid shows, or the current culture's short date. Blank means
/// "no bound". Anything else is an error the form reports, not a value that is quietly ignored
/// (the same contract as <see cref="SizeParser"/>).
/// </summary>
/// <remarks>
/// The form picks whole days, while <see cref="TCV.Search.SearchCriteria"/> and the presets hold
/// instants. The "to" day is therefore turned into the last instant of that day
/// (<see cref="EndOfDay"/>): a search up to 10 October must find a file saved at noon on the 10th.
/// </remarks>
public static class DateParser
{
    // Unambiguous forms first; the culture's own short date (10/1/2026 in en-US) last.
    private static readonly string[] Formats = ["yyyy-MM-dd", "yyyy-M-d", "dd.MM.yyyy", "d.M.yyyy"];

    /// <summary>True when the text is blank (<paramref name="day"/> null) or a valid date; the time of day is dropped.</summary>
    public static bool TryParse(string? text, out DateTime? day)
    {
        day = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var s = text.Trim();
        if (DateTime.TryParseExact(s, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact)
            || DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out exact))
        {
            day = exact.Date;
            return true;
        }

        return false;
    }

    /// <summary>The day as the form shows it (<c>2026-10-01</c>: the same in both languages); empty for no bound.</summary>
    public static string Format(DateTime? day) =>
        day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>The last instant of <paramref name="day"/>, so an upper bound includes that whole day.</summary>
    public static DateTime? EndOfDay(DateTime? day) => day is not { } d
        ? null
        : d.Date == DateTime.MaxValue.Date ? DateTime.MaxValue : d.Date.AddDays(1).AddTicks(-1);
}
