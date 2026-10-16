namespace TCV.App.Controls;

/// <summary>A cursor key in a list laid out in rows or columns: the arrows, the page keys, Home and End.</summary>
public enum GridMove
{
    Left,
    Right,
    Up,
    Down,
    PageUp,
    PageDown,
    First,
    Last,
}

/// <summary>Which way a wrapped list runs.</summary>
public enum WrapFlow
{
    /// <summary>Left to right in rows, scrolled vertically: the Thumbnails view, and a plain list (one item per row).</summary>
    Rows,

    /// <summary>Top to bottom in columns, scrolled horizontally: the Brief view, as in Total Commander.</summary>
    Columns,
}

/// <summary>
/// Cursor arithmetic for a list laid out in lines of items (rows of tiles in Thumbnails, columns of
/// names in Brief): where a key takes the cursor, and which items a viewport shows. WPF-free so it can
/// be tested; the view only measures how many items fit in a line and how many lines fit the viewport.
/// </summary>
public static class WrapGrid
{
    /// <summary>
    /// The index the cursor lands on. The keys along the flow (Left and Right in rows, Up and Down in
    /// columns) step through the list in order, so they wrap to the next or previous line; the keys
    /// across it (Up and Down in rows, Left and Right in columns) and the page keys keep the place in
    /// the line. A step across past the end lands on the last line — on the last item when that line is
    /// too short to have the place, as in Explorer and Total Commander — and a step off the first or
    /// last line leaves the cursor where it is. Returns -1 for an empty list.
    /// </summary>
    /// <param name="perLine">Items in a full line: tiles per row, or names per column.</param>
    /// <param name="linesPerPage">Whole lines the viewport shows: how far Page Up and Page Down go.</param>
    public static int Move(int index, int count, int perLine, GridMove move, int linesPerPage = 1, WrapFlow flow = WrapFlow.Rows)
    {
        if (count <= 0)
        {
            return -1;
        }

        perLine = Math.Max(1, perLine);
        linesPerPage = Math.Max(1, linesPerPage);
        index = Math.Clamp(index, 0, count - 1);
        var last = count - 1;

        // Columns are rows turned on their side: the arrows swap roles, the arithmetic stays.
        if (flow == WrapFlow.Columns)
        {
            move = move switch
            {
                GridMove.Up => GridMove.Left,
                GridMove.Down => GridMove.Right,
                GridMove.Left => GridMove.Up,
                GridMove.Right => GridMove.Down,
                _ => move,
            };
        }

        return move switch
        {
            GridMove.Left => Math.Max(0, index - 1),
            GridMove.Right => Math.Min(last, index + 1),
            GridMove.Up => Back(index, perLine, 1),
            GridMove.Down => Forward(index, last, perLine, 1),
            GridMove.PageUp => Back(index, perLine, linesPerPage),
            GridMove.PageDown => Forward(index, last, perLine, linesPerPage),
            GridMove.First => 0,
            GridMove.Last => last,
            _ => index,
        };
    }

    /// <summary>Lines of one size that fit the viewport whole: how far Page Up and Page Down go.</summary>
    public static int LinesPerPage(double viewportSize, double lineSize) =>
        lineSize > 0 ? Math.Max(1, (int)Math.Floor(viewportSize / lineSize)) : 1;

    /// <summary>
    /// The first and last index of the items a viewport shows in a list of rows, widened by
    /// <paramref name="marginRows"/> rows on each side, or (0, -1) when it shows nothing.
    /// </summary>
    /// <param name="firstRowTop">Top of the first row relative to the top of the viewport: zero at the
    /// start, negative once the list is scrolled down.</param>
    public static (int First, int Last) VisibleRange(
        double firstRowTop, double viewportHeight, double rowHeight, int columns, int count, int marginRows = 0)
    {
        if (count <= 0 || rowHeight <= 0 || viewportHeight <= 0)
        {
            return (0, -1);
        }

        columns = Math.Max(1, columns);
        var lastRow = (count - 1) / columns;

        // A row is visible when any part of it is inside [0, viewportHeight).
        var firstVisible = (int)Math.Floor(-firstRowTop / rowHeight);
        var lastVisible = (int)Math.Ceiling((viewportHeight - firstRowTop) / rowHeight) - 1;
        if (lastVisible < 0 || firstVisible > lastRow)
        {
            return (0, -1);
        }

        firstVisible = Math.Clamp(firstVisible - Math.Max(0, marginRows), 0, lastRow);
        lastVisible = Math.Clamp(lastVisible + Math.Max(0, marginRows), firstVisible, lastRow);
        return (firstVisible * columns, Math.Min(count - 1, ((lastVisible + 1) * columns) - 1));
    }

    // Back by whole lines, keeping the place in the line; from the first lines it stops on the first.
    private static int Back(int index, int perLine, int lines)
    {
        var target = index - (perLine * lines);
        return target >= 0 ? target : index % perLine;
    }

    // Forward by whole lines, keeping the place in the line. Past the end it stops on the last line: at
    // the same place when that line reaches it, otherwise on the last item. On the last line already, it stays.
    private static int Forward(int index, int last, int perLine, int lines)
    {
        var target = index + (perLine * lines);
        if (target <= last)
        {
            return target;
        }

        var lastLine = last / perLine;
        if (index / perLine == lastLine)
        {
            return index;
        }

        return Math.Min((lastLine * perLine) + (index % perLine), last);
    }
}
