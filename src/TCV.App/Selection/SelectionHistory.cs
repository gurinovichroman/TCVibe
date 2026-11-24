namespace TCV.App.Selection;

/// <summary>The masks Num+ remembers, newest first, as its drop-down offers them.</summary>
public static class SelectionHistory
{
    /// <summary>How many masks the list keeps.</summary>
    public const int Capacity = 20;

    /// <summary>
    /// Puts <paramref name="mask"/> on top of <paramref name="history"/>: a repeat moves up instead of
    /// appearing twice (case does not count, as in the masks themselves), and the oldest falls off.
    /// </summary>
    public static void Push(List<string> history, string? mask)
    {
        var text = mask?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        history.RemoveAll(m => string.Equals(m.Trim(), text, StringComparison.OrdinalIgnoreCase));
        history.Insert(0, text);
        if (history.Count > Capacity)
        {
            history.RemoveRange(Capacity, history.Count - Capacity);
        }
    }
}
