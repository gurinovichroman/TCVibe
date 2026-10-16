using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace TCV.App.Controls;

/// <summary>
/// What a drag over the workspace rail will do (6.79): a 2 px accent line in the gap where a dragged
/// workspace will land, or an accent frame round the rail for a folder that will become a workspace.
/// Drawn over the rail, never hit-tested, so the drag keeps seeing the buttons underneath.
/// </summary>
public sealed class RailDropAdorner : Adorner
{
    private readonly Brush _accent;
    private readonly Brush? _fill;
    private readonly double? _lineAt;
    private readonly double _lineWidth;

    private RailDropAdorner(UIElement adorned, Brush accent, Brush? fill, double? lineAt, double lineWidth)
        : base(adorned)
    {
        _accent = accent;
        _fill = fill;
        _lineAt = lineAt;
        _lineWidth = lineWidth;
        IsHitTestVisible = false;
    }

    /// <summary>
    /// A line as wide as the button's tile (<paramref name="width"/>, centred on the button),
    /// <paramref name="gap"/>/2 above it — or below it when <paramref name="below"/> — that is, in the
    /// middle of the gap to its neighbour.
    /// </summary>
    public static RailDropAdorner Line(FrameworkElement button, bool below, double gap, double width, Brush accent)
    {
        // Whole pixels: a 2 px line on a half pixel is drawn as a blurred 3 px one.
        var offset = Math.Floor(gap / 2);
        return new RailDropAdorner(button, accent, null, below ? button.ActualHeight + offset : -offset, width);
    }

    /// <summary>A frame round the whole rail, its inside tinted with <paramref name="fill"/>.</summary>
    public static RailDropAdorner Frame(FrameworkElement rail, Brush accent, Brush? fill) =>
        new(rail, accent, fill, null, 0);

    /// <summary>True when this adorner already shows the same thing, so a drag does not redraw it on every move.</summary>
    public bool Shows(UIElement target, bool below) =>
        ReferenceEquals(AdornedElement, target) && _lineAt is { } at && (at > 0) == below;

    /// <summary>True when this is the frame round <paramref name="rail"/>.</summary>
    public bool IsFrameOf(UIElement rail) => ReferenceEquals(AdornedElement, rail) && _lineAt is null;

    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = AdornedElement.RenderSize;
        if (_lineAt is { } y)
        {
            var width = Math.Min(_lineWidth, size.Width);
            var left = Math.Round((size.Width - width) / 2);
            drawingContext.DrawRoundedRectangle(_accent, null, new Rect(left, y - 1, width, 2), 1, 1);
            return;
        }

        var frame = new Rect(3.5, 3.5, Math.Max(0, size.Width - 7), Math.Max(0, size.Height - 7));
        if (_fill is not null)
        {
            // Light enough that the workspaces under it stay readable in both themes.
            drawingContext.PushOpacity(0.3);
            drawingContext.DrawRoundedRectangle(_fill, null, frame, 6, 6);
            drawingContext.Pop();
        }

        drawingContext.DrawRoundedRectangle(null, new Pen(_accent, 1), frame, 6, 6);
    }
}
