using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using TCV.App.Settings;

namespace TCV.App;

/// <summary>
/// The window opens where it was closed, as large as it was, maximised if it was. Windows' own
/// placement is kept (<c>GetWindowPlacement</c> / <c>SetWindowPlacement</c>), not Left/Top/Width/Height:
/// it is the restored rectangle even while maximised or minimised, it is in the monitor's pixels so a
/// second monitor at another scale comes back right, and Windows moves a window whose monitor was
/// unplugged back onto the screen.
/// </summary>
public partial class MainWindow
{
    /// <summary>Below this the saved rectangle is noise, not a window someone sized (physical pixels).</summary>
    private const int SmallestRestoredWidth = 200;
    private const int SmallestRestoredHeight = 150;

    private bool _placementRestored;

    private void InitializePlacement()
    {
        // A window opened for one workspace starts at the design size, centred: it does not write the
        // settings (TRAP-22), and opening it exactly over the main window would hide that one.
        var saved = _viewModel.IsSecondaryWindow ? null : _settings.Window;
        if (saved?.IsUsable(SmallestRestoredWidth, SmallestRestoredHeight) == true)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
        else
        {
            saved = null;
        }

        // Before the window is shown, so it never flashes at the design size first.
        SourceInitialized += (_, _) =>
        {
            if (saved is not null)
            {
                RestorePlacement(saved);
            }

            _placementRestored = true;
        };

        // Kept up to date in memory as the window moves; the session writes it (PersistState), the
        // way it writes the panels, so an update that ends the process (TRAP-72) keeps it too.
        LocationChanged += (_, _) => RememberPlacement();
        SizeChanged += (_, _) => RememberPlacement();
        StateChanged += (_, _) => RememberPlacement();
    }

    private void RestorePlacement(WindowPlacementSettings saved)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        // Only the rectangle, with the window still hidden: WPF shows it next, with its own WindowState,
        // and a maximised hwnd would be restored by that. Maximised, WPF then fills the monitor the
        // rectangle is on, and Restore comes back to the rectangle.
        var placement = new WindowPlacement
        {
            Length = Marshal.SizeOf<WindowPlacement>(),
            ShowCmd = SwHide,
            NormalPosition = new Rect { Left = saved.Left, Top = saved.Top, Right = saved.Right, Bottom = saved.Bottom },
            MinPosition = new NativePoint { X = -1, Y = -1 },
            MaxPosition = new NativePoint { X = -1, Y = -1 },
        };
        SetWindowPlacement(handle, ref placement);

        if (saved.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void RememberPlacement()
    {
        // The first events come from the window taking its design size; the saved one is not applied yet.
        if (!_placementRestored || _viewModel.IsSecondaryWindow)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (handle == IntPtr.Zero || !GetWindowPlacement(handle, ref placement))
        {
            return;
        }

        // Minimised, the window remembers whether it comes back maximised; that is what is kept.
        var maximized = placement.ShowCmd == SwShowMaximized
            || (placement.ShowCmd == SwShowMinimized && (placement.Flags & WpfRestoreToMaximized) != 0);
        var normal = placement.NormalPosition;
        if (_settings.Window is { } kept
            && kept.Left == normal.Left && kept.Top == normal.Top && kept.Right == normal.Right
            && kept.Bottom == normal.Bottom && kept.Maximized == maximized)
        {
            return;
        }

        _settings.Window = new WindowPlacementSettings
        {
            Left = normal.Left,
            Top = normal.Top,
            Right = normal.Right,
            Bottom = normal.Bottom,
            Maximized = maximized,
        };
    }

    // ── Win32 ────────────────────────────────────────────────────────────────

    private const int SwHide = 0;
    private const int SwShowMinimized = 2;
    private const int SwShowMaximized = 3;
    private const int WpfRestoreToMaximized = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public NativePoint MinPosition;
        public NativePoint MaxPosition;
        public Rect NormalPosition;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);
}
