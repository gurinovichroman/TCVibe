namespace TCV.App.Controls;

/// <summary>
/// Where the items of a wrapped list of one item size go, and which of them a viewport shows (6.46):
/// <see cref="VirtualizingWrapPanel"/> makes containers only for those. WPF-free so it can be tested.
/// </summary>
/// <remarks>
/// A line is a row of tiles (<see cref="WrapFlow.Rows"/>, Thumbnails, scrolled vertically) or a column
/// of names (<see cref="WrapFlow.Columns"/>, Brief, scrolled sideways). "Along" is the axis the list
/// scrolls on, "across" the one the items wrap on.
/// </remarks>
public readonly record struct WrapLayout(
    int Count, double ItemWidth, double ItemHeight, double ViewportWidth, double ViewportHeight, WrapFlow Flow)
{
    private double ItemAcross => Flow == WrapFlow.Rows ? ItemWidth : ItemHeight;

    private double ViewportAcross => Flow == WrapFlow.Rows ? ViewportWidth : ViewportHeight;

    /// <summary>How deep one line is along the scroll: a row's height, a column's width.</summary>
    public double LineSize => Flow == WrapFlow.Rows ? ItemHeight : ItemWidth;

    /// <summary>The viewport along the scroll.</summary>
    public double ViewportAlong => Flow == WrapFlow.Rows ? ViewportHeight : ViewportWidth;

    /// <summary>Items in a full line; at least one, and all of them when the viewport has no bound across.</summary>
    public int PerLine =>
        Count <= 0 || ItemAcross <= 0 ? 1
        : double.IsInfinity(ViewportAcross) ? Count
        : Math.Max(1, (int)Math.Floor((ViewportAcross + 0.5) / ItemAcross));

    public int Lines => Count <= 0 ? 0 : (Count + PerLine - 1) / PerLine;

    public double ExtentWidth => Flow == WrapFlow.Rows ? Math.Min(Count, PerLine) * ItemWidth : Lines * ItemWidth;

    public double ExtentHeight => Flow == WrapFlow.Rows ? Lines * ItemHeight : Math.Min(Count, PerLine) * ItemHeight;

    private double ExtentAlong => Flow == WrapFlow.Rows ? ExtentHeight : ExtentWidth;

    /// <summary>Whole lines in view: how far the page keys go.</summary>
    public int WholeLinesInView => WrapGrid.LinesPerPage(ViewportAlong, LineSize);

    /// <summary>The scroll offset along, kept inside the extent.</summary>
    public double ClampOffset(double offset) =>
        double.IsNaN(offset) ? 0 : Math.Clamp(offset, 0, Math.Max(0, ExtentAlong - ViewportAlong));

    /// <summary>
    /// The first and last index of the items in view at <paramref name="offset"/>, widened by
    /// <paramref name="marginLines"/> lines on each side, or (0, -1) when nothing is in view.
    /// </summary>
    public (int First, int Last) Range(double offset, int marginLines = 0) =>
        double.IsInfinity(ViewportAlong)
            ? (Count > 0 ? (0, Count - 1) : (0, -1))   // no viewport to scroll in: a list not made to scroll shows all
            : WrapGrid.VisibleRange(-offset, ViewportAlong, LineSize, PerLine, Count, marginLines);

    /// <summary>The top-left corner of an item, in the unscrolled list.</summary>
    public (double X, double Y) SlotOf(int index)
    {
        var line = index / PerLine;
        var place = index % PerLine;
        return Flow == WrapFlow.Rows
            ? (place * ItemWidth, line * ItemHeight)
            : (line * ItemWidth, place * ItemHeight);
    }

    /// <summary>
    /// The least scroll from <paramref name="offset"/> that shows the whole line of <paramref name="index"/>:
    /// unchanged when it is in view already, else the line lands on the near edge it was beyond.
    /// </summary>
    public double OffsetToShow(int index, double offset)
    {
        if (Count <= 0 || LineSize <= 0)
        {
            return offset;
        }

        var start = Math.Clamp(index, 0, Count - 1) / PerLine * LineSize;
        var end = start + LineSize;
        if (start < offset)
        {
            return ClampOffset(start);
        }

        return end > offset + ViewportAlong ? ClampOffset(end - ViewportAlong) : offset;
    }
}
