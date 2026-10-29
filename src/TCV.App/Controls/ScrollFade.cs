namespace TCV.App.Controls;

/// <summary>
/// How strongly a scrolled list fades out at each edge, from 0 (not at all) to 1 (fully at the edge):
/// in proportion to what is hidden past that edge, full once <c>length</c> pixels are. A list that
/// fits fades nowhere, and the fade grows as the first pixels scroll away instead of popping in — the
/// edge says "there is more this way" without a scroll bar (the workspace rail's).
/// </summary>
public static class ScrollFade
{
    public static (double Top, double Bottom) Edges(double offset, double viewport, double extent, double length)
    {
        if (viewport <= 0 || length <= 0)
        {
            return (0, 0);
        }

        var hiddenAbove = Math.Max(0, offset);
        var hiddenBelow = Math.Max(0, extent - viewport - offset);
        return (Math.Min(hiddenAbove, length) / length, Math.Min(hiddenBelow, length) / length);
    }
}
