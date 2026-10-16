using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class AgentApprovalDialog : TcvDialog
{
    public AgentApprovalDialog(AgentApprovalDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.CloseRequested += (_, _) => CloseWithResult(viewModel.DialogResult);

        // The prompt usually arrives while the user is looking at a terminal, with this application
        // minimised or buried. A modal window owned by a minimised owner is minimised with it, and
        // this one has no taskbar button of its own — so without this the prompt was simply not
        // there, and the agent's request timed out unseen.
        Loaded += (_, _) => BringToUser();

        Closed += (_, _) => viewModel.Dispose();
    }

    private void BringToUser()
    {
        if (Owner is { WindowState: WindowState.Minimized } owner)
            owner.WindowState = WindowState.Normal;

        Activate();

        // Activate() alone cannot take focus from another application; the taskbar flash is what
        // reliably tells the user that something is waiting.
        Flash(Owner);
        Flash(this);
    }

    private static void Flash(Window? window)
    {
        if (window is null) return;

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Handle = handle,
            Flags = FlashAll | FlashTimerNoForeground,
            Count = uint.MaxValue,
            Timeout = 0,
        };

        FlashWindowEx(ref info);
    }

    private const uint FlashAll = 3;                 // FLASHW_ALL: caption and taskbar button
    private const uint FlashTimerNoForeground = 12;  // FLASHW_TIMERNOFG: until the window is foreground

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Handle;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);
}
