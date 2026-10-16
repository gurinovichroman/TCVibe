using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TCV.App.Controls;

/// <summary>
/// The pointer shown while files are dragged between panels: an arrow with a small badge that says
/// what the drop will do — the copy icon or an arrow for a move. Windows draws only a faint "+" for a
/// copy and nothing at all for a move, so a drag that Shift or the right button had turned into a move
/// looked exactly like a copy.
/// </summary>
/// <remarks>
/// The cursor is a 32-bit PNG wrapped in a .cur header, built in memory and cached per effect, screen
/// scale and theme. The icons and colours are the app's own (<c>Themes/Icons.xaml</c>, the theme's card,
/// border, accent and warning colours), so the badge matches the toolbar. Other effects (link, none)
/// keep the system cursors.
/// </remarks>
public static class DragCursor
{
    private const double IconSize = 10;
    private const double BadgeSize = 16;

    private static readonly Dictionary<(DragDropEffects Effect, double Scale, Color Fill, Color Accent), Cursor> Cache = [];

    private static readonly Geometry Arrow = Geometry.Parse("M0,0 L0,16 L3.8,12.6 L6.6,19 L9.2,17.9 L6.4,11.6 L11.4,11.6 Z");

    /// <summary>The cursor for <paramref name="effect"/>, or null to leave the system one.</summary>
    public static Cursor? For(DragDropEffects effect, double scale)
    {
        string icon, accentKey;

        // Near the edge of a scrollable target OLE adds Scroll to the effect; it is not part of the verb.
        effect &= ~DragDropEffects.Scroll;
        switch (effect)
        {
            case DragDropEffects.Copy:
                icon = "IconCopy";
                accentKey = "AccentColor";
                break;
            case DragDropEffects.Move:
                icon = "IconArrowRight";
                accentKey = "WarningColor";
                break;
            default:
                return null;
        }

        var resources = Application.Current;
        var fill = resources?.TryFindResource("CardColor") is Color card ? card : Color.FromRgb(0x16, 0x17, 0x19);
        var border = resources?.TryFindResource("BorderColor") is Color line ? line : Color.FromRgb(0x29, 0x2B, 0x2F);
        var accent = resources?.TryFindResource(accentKey) is Color tint ? tint : Color.FromRgb(0x8D, 0xD8, 0x6D);
        scale = Math.Max(1, Math.Round(scale, 2));
        if (Cache.TryGetValue((effect, scale, fill, accent), out var cached))
        {
            return cached;
        }

        var cursor = Build(resources?.TryFindResource(icon) as Geometry, fill, border, accent, scale);
        if (cursor is not null)
        {
            Cache[(effect, scale, fill, accent)] = cursor;
        }

        return cursor;
    }

    private static Cursor? Build(Geometry? icon, Color fill, Color border, Color accent, double scale)
    {
        const double width = 11 + BadgeSize + 2;
        const double height = 10 + BadgeSize + 2;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawGeometry(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)), 1) { LineJoin = PenLineJoin.Round }, Arrow);

            var badge = new Rect(11, 10, BadgeSize, BadgeSize);
            dc.DrawRoundedRectangle(new SolidColorBrush(fill), new Pen(new SolidColorBrush(border), 1), badge, 4, 4);

            if (icon is not null)
            {
                var pen = new Pen(new SolidColorBrush(accent), 2.6)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round,
                };
                var k = IconSize / 24;
                dc.PushTransform(new TranslateTransform(badge.X + (BadgeSize - IconSize) / 2, badge.Y + (BadgeSize - IconSize) / 2));
                dc.PushTransform(new ScaleTransform(k, k));
                dc.DrawGeometry(null, pen, icon);
                dc.Pop();
                dc.Pop();
            }
        }

        var pixelWidth = (int)Math.Ceiling(width * scale);
        var pixelHeight = (int)Math.Ceiling(height * scale);
        if (pixelWidth > 256 || pixelHeight > 256)
        {
            return null;
        }

        var scaled = new DrawingVisual();
        using (var dc = scaled.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawDrawing(visual.Drawing);
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(scaled);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var png = new MemoryStream();
        encoder.Save(png);

        // ICONDIR + one ICONDIRENTRY (planes/bit count carry the hot spot in a cursor), then the PNG.
        using var cur = new MemoryStream();
        using (var writer = new BinaryWriter(cur, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)2);
            writer.Write((ushort)1);
            writer.Write((byte)pixelWidth);
            writer.Write((byte)pixelHeight);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((uint)png.Length);
            writer.Write((uint)22);
            writer.Write(png.ToArray());
        }

        cur.Position = 0;
        try
        {
            return new Cursor(cur);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
