namespace TCV.App.Viewer;

/// <summary>
/// Case-insensitive find with wrap-around, the logic behind the viewer's Find box. Pure, so the
/// cycling rules — next continues after the current match, previous before it, both wrap — are tested
/// without a window.
/// </summary>
public static class TextFinder
{
    /// <summary>
    /// Index of the next match of <paramref name="query"/> at or after <paramref name="from"/>
    /// (forward) or ending at or before <paramref name="from"/> (backward), wrapping around once.
    /// Returns -1 when the text has no match at all; <paramref name="wrapped"/> says the search had to
    /// start over from the other end of the text.
    /// </summary>
    public static int Find(string text, string query, int from, bool forward, out bool wrapped)
    {
        wrapped = false;
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query))
        {
            return -1;
        }

        from = Math.Clamp(from, 0, text.Length);

        int index;
        if (forward)
        {
            index = text.IndexOf(query, from, StringComparison.OrdinalIgnoreCase);
            if (index < 0 && from > 0)
            {
                index = text.IndexOf(query, 0, StringComparison.OrdinalIgnoreCase);
                wrapped = index >= 0;
            }
        }
        else
        {
            index = from == 0 ? -1 : text.LastIndexOf(query, from - 1, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                index = text.LastIndexOf(query, text.Length - 1, StringComparison.OrdinalIgnoreCase);
                wrapped = index >= 0;
            }
        }

        return index;
    }
}
