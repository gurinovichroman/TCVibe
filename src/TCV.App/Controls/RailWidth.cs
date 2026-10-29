namespace TCV.App.Controls;

/// <summary>
/// How wide the workspace rail opened wide is. Unset (0), it is <see cref="Automatic"/> wide and reaches
/// the side bar's edge, so no sliver of tree is left beside it; dragged, it is what the user made it,
/// within <see cref="Min"/> and the room the window has. A drag that ends near the side bar's edge
/// sticks to it and goes back to following it.
/// </summary>
public static class RailWidth
{
    /// <summary>Room for a long project name.</summary>
    public const double Automatic = 240;

    /// <summary>Narrower, the rail opened wide would show less than the narrow one with its tooltips.</summary>
    public const double Min = 160;

    /// <summary>Wider says nothing more and covers the panels.</summary>
    public const double Max = 600;

    /// <summary>How close to the side bar's edge a drag sticks to it.</summary>
    public const double Snap = 8;

    /// <summary>The width with <paramref name="stored"/> (0 = automatic), <paramref name="sideBarEdge"/>
    /// (0 without a side bar) and <paramref name="room"/> — the most the window gives.</summary>
    public static double Expanded(double stored, double sideBarEdge, double room) =>
        Clamp(stored > 0 ? stored : Math.Max(Automatic, sideBarEdge), room);

    /// <summary>The width a drag to <paramref name="raw"/> gives, and the value to keep for it: 0 when it
    /// is the automatic width (it stuck to the side bar's edge, or let go right on it).</summary>
    public static (double Width, double Stored) Dragged(double raw, double sideBarEdge, double room)
    {
        var width = sideBarEdge >= Min && Math.Abs(raw - sideBarEdge) <= Snap ? sideBarEdge : raw;
        width = Clamp(width, room);
        return (width, Math.Abs(width - Expanded(0, sideBarEdge, room)) < 0.5 ? 0 : width);
    }

    private static double Clamp(double width, double room) =>
        Math.Round(Math.Clamp(width, Min, Math.Max(Min, Math.Min(Max, room))));
}
