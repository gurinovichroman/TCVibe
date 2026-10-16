using System.Windows;
using System.Windows.Media;
using TCV.App.Workspaces;

namespace TCV.App.Settings;

/// <summary>
/// What the appearance settings write over the theme (6.33): the accent colour, the file lists'
/// font and which parts of the window chrome are shown.
/// </summary>
/// <remarks>
/// Chrome visibility is a resource rather than a view-model property on purpose. It is the same for
/// both panels and for parts no view model owns (the F-key bar), and <see cref="ThemeManager"/>
/// already rebuilds the theme dictionary on every settings save — so one write reaches every element
/// through <c>{DynamicResource}</c>, with no layout object threaded through the panels. The defaults
/// live in <c>Themes/Base.xaml</c>; this class only decides the values, so it is testable without an
/// application object.
/// </remarks>
public static class AppearanceResources
{
    /// <summary>The colours of the theme being loaded that a custom accent is fitted to.</summary>
    public readonly record struct ThemeTones(Color Selection, Color Foreground);

    public const double MinPanelFontSize = 8;
    public const double MaxPanelFontSize = 24;
    public const double DefaultPanelFontSize = 11;

    public const string AccentColorKey = "AccentColor";
    public const string AccentBrushKey = "AccentBrush";

    /// <summary>Text drawn on an accent fill: a selected combo item, a pressed or focused button.</summary>
    public const string AccentForegroundBrushKey = "AccentForegroundBrush";

    /// <summary>The selection slab (a marked row, a chosen item, a switch that is on), tinted by the accent.</summary>
    public const string SelectionColorKey = "SelectionColor";
    public const string SelectionBrushKey = "SelectionBrush";

    /// <summary>
    /// The least contrast the theme's text keeps on a selection derived from the user's accent: the
    /// marked rows are read, not glanced at (WCAG AAA for body text).
    /// </summary>
    public const double MinSelectionTextContrast = 7.0;

    /// <summary>The bundled IBM Plex Sans (Fonts/*.ttf), as Base.xaml names it; Segoe UI covers what it lacks.</summary>
    public const string DefaultFontFamily = "pack://application:,,,/TCV.App;component/Fonts/#IBM Plex Sans, Segoe UI";

    public const string PanelFontFamilyKey = "PanelFontFamily";
    public const string PanelFontSizeKey = "PanelFontSize";

    public const string FunctionKeyBarVisibilityKey = "ChromeFunctionKeyBarVisibility";
    public const string DriveBarVisibilityKey = "ChromeDriveBarVisibility";
    public const string TabBarVisibilityKey = "ChromeTabBarVisibility";
    public const string PathBarVisibilityKey = "ChromePathBarVisibility";
    public const string ViewToolbarVisibilityKey = "ChromeViewToolbarVisibility";
    public const string StatusBarVisibilityKey = "ChromeStatusBarVisibility";
    public const string ToolbarVisibilityKey = "ChromeToolbarVisibility";
    public const string CommandLineVisibilityKey = "ChromeCommandLineVisibility";
    public const string AppStatusBarVisibilityKey = "ChromeAppStatusBarVisibility";

    /// <summary>The design's line icons in the file lists; the opposite of <see cref="SystemFileIconVisibilityKey"/>.</summary>
    public const string LineFileIconVisibilityKey = "LineFileIconVisibility";

    /// <summary>Windows' own icons in the file lists (<see cref="ThemeSettings.SystemFileIcons"/>).</summary>
    public const string SystemFileIconVisibilityKey = "SystemFileIconVisibility";

    /// <summary>
    /// A size the file list can use: whole points within the bounds. A hand-edited 0, 300 or NaN in
    /// <c>settings.json</c> would otherwise leave the panels unreadable or fail the layout.
    /// </summary>
    public static double ClampFontSize(double size) =>
        double.IsFinite(size)
            ? Math.Clamp(Math.Round(size, MidpointRounding.AwayFromZero), MinPanelFontSize, MaxPanelFontSize)
            : DefaultPanelFontSize;

    /// <summary>
    /// The pairs to write over a freshly loaded theme dictionary. The font family and the accent are
    /// present only when set (the accent also only when it parses), so "empty" leaves the theme's own.
    /// With <paramref name="tones"/> (the theme being loaded), a custom accent also re-tints the
    /// selection slab, which would otherwise stay the theme's green under a blue accent bar.
    /// </summary>
    public static IReadOnlyDictionary<string, object> Build(AppSettings settings, ThemeTones? tones = null)
    {
        var theme = settings.Theme;
        var layout = settings.Layout;

        var resources = new Dictionary<string, object>
        {
            [PanelFontSizeKey] = ClampFontSize(theme.PanelFontSize),
            [FunctionKeyBarVisibilityKey] = VisibleWhen(layout.ShowFunctionKeyBar),
            [DriveBarVisibilityKey] = VisibleWhen(layout.ShowDriveBar),
            [TabBarVisibilityKey] = VisibleWhen(layout.ShowTabBar),
            [PathBarVisibilityKey] = VisibleWhen(layout.ShowPathBar),
            [ViewToolbarVisibilityKey] = VisibleWhen(layout.ShowViewToolbar),
            [StatusBarVisibilityKey] = VisibleWhen(layout.ShowStatusBar),
            [ToolbarVisibilityKey] = VisibleWhen(layout.ShowToolbar),
            [CommandLineVisibilityKey] = VisibleWhen(layout.ShowCommandLine),
            [AppStatusBarVisibilityKey] = VisibleWhen(layout.ShowAppStatusBar),
            [LineFileIconVisibilityKey] = VisibleWhen(!theme.SystemFileIcons),
            [SystemFileIconVisibilityKey] = VisibleWhen(theme.SystemFileIcons),
        };

        // A family that is not installed (a settings.json copied from another machine) is fine: WPF
        // draws with a system font instead, and the name is kept for the machine that has it.
        if (!string.IsNullOrWhiteSpace(theme.PanelFontFamily))
        {
            resources[PanelFontFamilyKey] = new FontFamily(theme.PanelFontFamily.Trim());
        }

        if (WorkspaceColor.TryParseRgb(theme.AccentColor, out var r, out var g, out var b))
        {
            var accent = Color.FromRgb(r, g, b);
            resources[AccentColorKey] = accent;
            resources[AccentBrushKey] = Frozen(accent);
            resources[AccentForegroundBrushKey] = Frozen(ForegroundOn(accent));

            if (tones is { } loaded)
            {
                var selection = SelectionFor(accent, loaded.Selection, loaded.Foreground);
                resources[SelectionColorKey] = selection;
                resources[SelectionBrushKey] = Frozen(selection);
            }
        }

        return resources;
    }

    /// <summary>
    /// The selection slab for a custom accent: the accent's hue at the lightness and (at most) the
    /// chroma of the theme's own slab, measured in OKLCH, so the slab reads as the accent's colour
    /// but sits exactly as far from the panel background and the text as the designed one does. A
    /// dark navy or a pale yellow accent would make a mixed-in slab vanish or glare; holding the
    /// lightness is what keeps every accent usable. If the hue still costs contrast (WCAG luminance
    /// and OKLab lightness disagree a little away from grey), the lightness moves away from the text
    /// until <see cref="MinSelectionTextContrast"/> (or the theme's own contrast, if lower) holds.
    /// </summary>
    public static Color SelectionFor(Color accent, Color themeSelection, Color foreground)
    {
        var (lightness, themeChroma, _) = Oklch(themeSelection);
        var (_, accentChroma, hue) = Oklch(accent);
        var chroma = Math.Min(accentChroma, themeChroma);

        var target = Math.Min(MinSelectionTextContrast, Contrast(foreground, themeSelection));
        var away = Oklch(foreground).L > lightness ? -0.005 : 0.005;

        var result = InGamut(lightness, chroma, hue);
        for (var step = 0; step < 100 && Contrast(foreground, result) < target; step++)
        {
            lightness = Math.Clamp(lightness + away, 0, 1);
            result = InGamut(lightness, chroma, hue);
        }

        return result;
    }

    /// <summary>WCAG 2 contrast ratio of two colours, 1 to 21.</summary>
    public static double Contrast(Color a, Color b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>
    /// The colour at the given OKLCH lightness and hue with the most chroma up to
    /// <paramref name="chroma"/> that sRGB can show.
    /// </summary>
    private static Color InGamut(double lightness, double chroma, double hue)
    {
        for (; chroma > 0.0005; chroma *= 0.9)
        {
            if (TryFromOklch(lightness, chroma, hue, out var color))
            {
                return color;
            }
        }

        TryFromOklch(lightness, 0, hue, out var grey);
        return grey;
    }

    // OKLab (Björn Ottosson, 2020): the space the design tokens are written in (docs/DESIGN.md).

    private static (double L, double C, double H) Oklch(Color color)
    {
        double r = Linear(color.R), g = Linear(color.G), b = Linear(color.B);

        var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);

        var lightness = 0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s;
        var a = 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s;
        var bb = 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s;
        return (lightness, Math.Sqrt(a * a + bb * bb), Math.Atan2(bb, a));
    }

    private static bool TryFromOklch(double lightness, double chroma, double hue, out Color color)
    {
        var a = chroma * Math.Cos(hue);
        var b = chroma * Math.Sin(hue);

        var l = Math.Pow(lightness + 0.3963377774 * a + 0.2158037573 * b, 3);
        var m = Math.Pow(lightness - 0.1055613458 * a - 0.0638541728 * b, 3);
        var s = Math.Pow(lightness - 0.0894841775 * a - 1.2914855480 * b, 3);

        var red = 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
        var green = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
        var blue = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;

        const double slack = 0.0001;
        var inside = red is >= -slack and <= 1 + slack && green is >= -slack and <= 1 + slack && blue is >= -slack and <= 1 + slack;
        color = Color.FromRgb(Encode(red), Encode(green), Encode(blue));
        return inside;
    }

    private static byte Encode(double linear)
    {
        var c = Math.Clamp(linear, 0, 1);
        var srgb = c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
        return (byte)Math.Round(srgb * 255, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// White or black, whichever has the higher WCAG contrast ratio against
    /// <paramref name="background"/>. The themes draw white on their own blue; on a yellow or a pale
    /// green chosen by the user, white text would all but vanish.
    /// </summary>
    public static Color ForegroundOn(Color background)
    {
        var luminance = RelativeLuminance(background);
        var withWhite = 1.05 / (luminance + 0.05);
        var withBlack = (luminance + 0.05) / 0.05;
        return withWhite >= withBlack ? Colors.White : Colors.Black;
    }

    /// <summary>WCAG 2 relative luminance of an sRGB colour: 0 for black, 1 for white.</summary>
    public static double RelativeLuminance(Color color) =>
        0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);

    private static double Linear(byte channel)
    {
        var c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static Visibility VisibleWhen(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
