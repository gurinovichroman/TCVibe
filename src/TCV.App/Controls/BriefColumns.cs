namespace TCV.App.Controls;

/// <summary>
/// The width of the Brief view's columns: one for all of them, as in Total Commander, set by the longest
/// name in the folder within the row's limits. Each column used to be as wide as its own longest name.
/// The numbers describe the Brief item template in FileListControl.xaml. WPF-free, so it can be tested.
/// </summary>
public static class BriefColumns
{
    /// <summary>The narrowest row: the template's MinWidth.</summary>
    public const double MinRowWidth = 160;

    /// <summary>The widest row: the template's MaxWidth; a longer name is cut with an ellipsis.</summary>
    public const double MaxRowWidth = 240;

    /// <summary>The icon (22) and the gap after it (3).</summary>
    public const double IconWidth = 25;

    /// <summary>Around the row: its margin (4 + 4) and the item's padding (2 + 2).</summary>
    public const double ItemChrome = 12;

    /// <summary>
    /// How many names are measured, the longest by characters: measuring all of 15 000 would cost on
    /// every load what the column gains, and a name short in characters rarely outgrows a long one.
    /// </summary>
    public const int MeasuredNames = 32;

    /// <summary>The width of one item, and so of every column, for a folder holding <paramref name="names"/>.</summary>
    /// <param name="measure">Width of a name in the list's font.</param>
    public static double ItemWidth(IEnumerable<string> names, Func<string, double> measure)
    {
        var widest = 0d;
        foreach (var name in names.OrderByDescending(n => n.Length).Take(MeasuredNames))
        {
            widest = Math.Max(widest, measure(name));
            if (IconWidth + widest >= MaxRowWidth)
            {
                break;
            }
        }

        // A pixel to spare: the text box rounds its width up, and a name a hair too wide gets an ellipsis.
        return Math.Clamp(Math.Ceiling(IconWidth + widest) + 1, MinRowWidth, MaxRowWidth) + ItemChrome;
    }
}
