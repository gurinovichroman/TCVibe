using System.Windows;
using System.Windows.Interop;

namespace TCV.App;

/// <summary>
/// Snap layouts on the own title bar (6.77). Windows 11 opens its layout flyout when the pointer rests
/// on the system's maximise button, which it recognises by the hit test answer HTMAXBUTTON. The
/// window draws its own caption (ADR-0030), so the answer is given here for the drawn button.
/// </summary>
/// <remarks>
/// Once the hit test says "maximise button", the pointer over it is non-client: WPF sees no mouse
/// over the button and no click, so hover and press are painted here and the click is taken from
/// WM_NCLBUTTONUP. Letting DefWindowProc have the press would draw a classic caption button over ours.
/// The hook is added on Loaded, after WindowChrome's own: hooks run newest first, so this one answers
/// before WindowChrome would call the button's area caption. Windows 10 has no flyout and just gets a
/// maximise button that works as before.
/// </remarks>
public partial class MainWindow
{
    private const int WmNcHitTest = 0x0084;
    private const int WmNcMouseMove = 0x00A0;
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmNcLButtonUp = 0x00A2;
    private const int WmNcMouseLeave = 0x02A2;
    private const int HtMaxButton = 9;

    private bool _snapHooked;
    private bool _maximizeHovered;
    private bool _maximizePressed;

    private void InitializeSnapLayouts()
    {
        Loaded += (_, _) =>
        {
            if (_snapHooked || PresentationSource.FromVisual(this) is not HwndSource source)
            {
                return;
            }

            source.AddHook(SnapLayoutsHook);
            _snapHooked = true;
        };
    }

    private IntPtr SnapLayoutsHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmNcHitTest:
                if (IsOverMaximizeButton(lParam))
                {
                    PaintMaximizeButton(hover: true);
                    handled = true;
                    return HtMaxButton;
                }

                PaintMaximizeButton(hover: false);
                break;

            case WmNcMouseMove when wParam.ToInt32() != HtMaxButton:
            case WmNcMouseLeave:
                _maximizePressed = false;
                PaintMaximizeButton(hover: false);
                break;

            case WmNcLButtonDown when wParam.ToInt32() == HtMaxButton:
                _maximizePressed = true;
                PaintMaximizeButton(hover: true);
                handled = true;
                break;

            case WmNcLButtonUp when wParam.ToInt32() == HtMaxButton:
                if (_maximizePressed)
                {
                    _maximizePressed = false;
                    OnMaximizeClick(MaximizeButton, new RoutedEventArgs());
                }

                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    private bool IsOverMaximizeButton(IntPtr lParam)
    {
        if (!MaximizeButton.IsVisible || PresentationSource.FromVisual(MaximizeButton) is null)
        {
            return false;
        }

        // Screen pixels, signed: a second monitor left of the first has negative coordinates.
        var value = lParam.ToInt64();
        var screen = new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
        var local = MaximizeButton.PointFromScreen(screen);
        return local.X >= 0 && local.Y >= 0 && local.X < MaximizeButton.ActualWidth && local.Y < MaximizeButton.ActualHeight;
    }

    /// <summary>The button's hover and press, which its style can no longer see (see remarks).</summary>
    private void PaintMaximizeButton(bool hover)
    {
        if (!hover)
        {
            if (_maximizeHovered)
            {
                _maximizeHovered = false;
                MaximizeButton.ClearValue(BackgroundProperty);
                MaximizeButton.ClearValue(ForegroundProperty);
            }

            return;
        }

        _maximizeHovered = true;
        MaximizeButton.SetResourceReference(BackgroundProperty, _maximizePressed ? "PressedBrush" : "HoverBrush");
        MaximizeButton.SetResourceReference(ForegroundProperty, "ForegroundBrush");
    }
}
