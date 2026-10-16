using System.Windows;
using System.Windows.Controls;

namespace TCV.App.Controls;

/// <summary>
/// A row whose first child takes the room the others leave, and whose other children sit on the right,
/// shown from the second while they fit whole beside the first child's own width; the rest are hidden
/// instead of cut in half. A first child with less room than its <see cref="FrameworkElement.MinWidth"/>
/// and margin is hidden too, and the others then have the row. The title bar is built on it: the workspace box has
/// the room first, and the notes after it (the drop stack, a guest window's warning, the version) give
/// way on a narrow window.
/// </summary>
public sealed class FitPanel : Panel
{
    private int _shown;
    private double _notesWidth;
    private bool _firstShown;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count == 0)
        {
            return default;
        }

        var unbounded = new Size(double.PositiveInfinity, availableSize.Height);
        var first = InternalChildren[0];
        first.Measure(unbounded);

        var widths = new double[InternalChildren.Count - 1];
        var height = first.DesiredSize.Height;
        for (var i = 0; i < widths.Length; i++)
        {
            var child = InternalChildren[i + 1];
            child.Measure(unbounded);
            widths[i] = child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }

        // DesiredSize counts the margin and MinWidth does not: a slot of just MinWidth would cut the child.
        var firstMin = first is FrameworkElement element
            ? element.MinWidth + element.Margin.Left + element.Margin.Right
            : 0;
        (_shown, var firstWidth) = Split(first.DesiredSize.Width, firstMin, widths, availableSize.Width);
        _firstShown = firstWidth > 0;
        _notesWidth = widths.Take(_shown).Sum();

        // Measured again within its share, so a long name trims rather than runs under the notes.
        first.Measure(new Size(firstWidth, availableSize.Height));
        var width = (_firstShown ? first.DesiredSize.Width : 0) + _notesWidth;
        return new Size(Math.Min(width, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count == 0)
        {
            return finalSize;
        }

        // An empty slot clips a child away: it neither draws nor takes a click.
        var firstWidth = _firstShown ? Math.Max(0, finalSize.Width - _notesWidth) : 0;
        InternalChildren[0].Arrange(new Rect(0, 0, firstWidth, _firstShown ? finalSize.Height : 0));

        var x = finalSize.Width - _notesWidth;
        for (var i = 1; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            if (i - 1 < _shown)
            {
                child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
                x += child.DesiredSize.Width;
            }
            else
            {
                child.Arrange(new Rect(x, 0, 0, 0));
            }
        }

        return finalSize;
    }

    /// <summary>
    /// How many of the notes are shown beside a first child that wants <paramref name="firstWant"/> and
    /// needs at least <paramref name="firstMin"/>, and how wide the first child's share is: all that the
    /// shown notes leave, or 0 when even its minimum does not fit and it is hidden.
    /// </summary>
    public static (int Shown, double FirstWidth) Split(
        double firstWant, double firstMin, IReadOnlyList<double> notes, double available)
    {
        if (available < firstMin)
        {
            return (CountFitting(notes, available), 0);
        }

        var shown = CountFitting(notes, available - firstWant);
        return (shown, Math.Max(0, available - notes.Take(shown).Sum()));
    }

    /// <summary>How many of the leading widths fit into <paramref name="available"/> together.</summary>
    public static int CountFitting(IReadOnlyList<double> widths, double available)
    {
        double used = 0;
        for (var i = 0; i < widths.Count; i++)
        {
            used += widths[i];
            if (used > available)
            {
                return i;
            }
        }

        return widths.Count;
    }
}
