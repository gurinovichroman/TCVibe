using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using TCV.App.Settings;

namespace TCV.App.Controls;

/// <summary>
/// Paints the native frame of the dialogs in the theme (6.76): dark or light caption buttons, the
/// caption in the dialog's own card colour, its text and the outline in the theme's. Without it a
/// light theme under a dark Windows (or the other way round) put a black bar over a white dialog.
/// The main window draws its own title bar (ADR-0030, <see cref="WindowChrome"/>) and is left alone.
/// </summary>
/// <remarks>
/// The colours are read from the window's resources, so a theme switch or a custom accent needs no
/// list of keys here, only <see cref="ApplyToOpenWindows"/>. Caption, text and border colours are
/// Windows 11 (build 22000) attributes; Windows 10 takes only the dark mode and ignores the rest,
/// which is the best it can show. Every call is best effort: a refused attribute leaves the frame
/// as Windows draws it.
/// </remarks>
public static class TitleBarTheme
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmBorderColor = 34;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    /// <summary>Themes the window's frame as soon as it has one, before it is first shown.</summary>
    public static void Follow(Window window)
    {
        window.SourceInitialized += (_, _) => Apply(window);
    }

    /// <summary>Re-themes every open dialog: the theme or the accent changed under them.</summary>
    public static void ApplyToOpenWindows()
    {
        if (Application.Current is null)
        {
            return;
        }

        foreach (Window window in Application.Current.Windows)
        {
            Apply(window);
        }
    }

    public static void Apply(Window window)
    {
        if (WindowChrome.GetWindowChrome(window) is not null)
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero
            || window.TryFindResource("CardColor") is not Color caption
            || window.TryFindResource("ForegroundColor") is not Color text
            || window.TryFindResource("BorderColor") is not Color border)
        {
            return;
        }

        var dark = IsDark(caption) ? 1 : 0;
        if (DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(handle, DwmUseImmersiveDarkModeBefore20H1, ref dark, sizeof(int));
        }

        Set(handle, DwmCaptionColor, caption);
        Set(handle, DwmTextColor, text);
        Set(handle, DwmBorderColor, border);
    }

    /// <summary>A caption this dark wants Windows' dark caption buttons (white glyphs).</summary>
    public static bool IsDark(Color caption) => AppearanceResources.RelativeLuminance(caption) < 0.18;

    /// <summary>A Win32 COLORREF: 0x00BBGGRR.</summary>
    public static int ColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    private static void Set(IntPtr handle, int attribute, Color color)
    {
        var value = ColorRef(color);
        DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
